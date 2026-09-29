use crate::strategy::{Strategy, StrategyDatabase, StrategyError, StrategySummary};
use chrono::{DateTime, Utc};
use serde::Serialize;
use std::{
    fs::{self, OpenOptions},
    io::Write,
    path::PathBuf,
    process::{Child, Command, Stdio},
    sync::Mutex,
};
use thiserror::Error;

#[cfg(windows)]
use std::os::windows::process::CommandExt;

#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

const DEFAULT_TARGETS: &str = include_str!("../../lists/targets.txt");
const DEFAULT_EXCLUDES: &str = include_str!("../../lists/exclude-user.txt");

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct EngineStatus {
    pub running: bool,
    pub pid: Option<u32>,
    pub strategy_id: Option<String>,
    pub started_at: Option<DateTime<Utc>>,
    pub last_error: Option<String>,
}

impl Default for EngineStatus {
    fn default() -> Self {
        Self {
            running: false,
            pid: None,
            strategy_id: None,
            started_at: None,
            last_error: None,
        }
    }
}

struct RuntimeState {
    child: Option<Child>,
    status: EngineStatus,
}

pub struct EngineController {
    engine_dir: PathBuf,
    data_dir: PathBuf,
    log_dir: PathBuf,
    strategies: StrategyDatabase,
    runtime: Mutex<RuntimeState>,
}

#[derive(Debug, Error)]
pub enum ControllerError {
    #[error(transparent)]
    Strategy(#[from] StrategyError),
    #[error("engine component is not installed; place winws.exe in {0}")]
    MissingEngine(String),
    #[error("engine path is outside the managed component directory")]
    UnsafeEnginePath,
    #[error("failed to access the engine component: {0}")]
    Io(#[from] std::io::Error),
    #[error("engine state lock is unavailable")]
    Lock,
    #[error("the engine is already running")]
    AlreadyRunning,
}

impl EngineController {
    pub fn new(data_dir: PathBuf) -> Result<Self, ControllerError> {
        let engine_dir = data_dir.join("engine");
        let log_dir = data_dir.join("logs");
        fs::create_dir_all(&engine_dir)?;
        fs::create_dir_all(&log_dir)?;
        let list_dir = data_dir.join("lists");
        fs::create_dir_all(&list_dir)?;
        write_seed_if_missing(&list_dir.join("targets.txt"), DEFAULT_TARGETS)?;
        write_seed_if_missing(&list_dir.join("exclude-user.txt"), DEFAULT_EXCLUDES)?;

        Ok(Self {
            engine_dir,
            data_dir,
            log_dir,
            strategies: StrategyDatabase::load_embedded()?,
            runtime: Mutex::new(RuntimeState {
                child: None,
                status: EngineStatus::default(),
            }),
        })
    }

    pub fn list_strategies(&self) -> Vec<StrategySummary> {
        self.strategies.summaries()
    }

    pub fn status(&self) -> Result<EngineStatus, ControllerError> {
        let mut runtime = self.runtime.lock().map_err(|_| ControllerError::Lock)?;
        refresh_runtime(&mut runtime)?;
        Ok(runtime.status.clone())
    }

    pub fn start(&self, strategy_id: &str) -> Result<EngineStatus, ControllerError> {
        let strategy = self.strategies.get(strategy_id)?;
        let executable = self.resolve_executable()?;
        let args = self.expand_args(strategy);
        let log_path = self.log_dir.join("engine.log");
        let stdout = OpenOptions::new().create(true).append(true).open(&log_path)?;
        let stderr = stdout.try_clone()?;

        let mut runtime = self.runtime.lock().map_err(|_| ControllerError::Lock)?;
        refresh_runtime(&mut runtime)?;
        if runtime.child.is_some() {
            return Err(ControllerError::AlreadyRunning);
        }

        let mut command = Command::new(executable);
        command
            .args(args)
            .current_dir(&self.engine_dir)
            .stdin(Stdio::null())
            .stdout(Stdio::from(stdout))
            .stderr(Stdio::from(stderr));
        #[cfg(windows)]
        command.creation_flags(CREATE_NO_WINDOW);

        let child = command.spawn()?;
        let status = EngineStatus {
            running: true,
            pid: Some(child.id()),
            strategy_id: Some(strategy.id.clone()),
            started_at: Some(Utc::now()),
            last_error: None,
        };
        runtime.child = Some(child);
        runtime.status = status.clone();
        self.write_audit("engine_started", Some(strategy_id));
        Ok(status)
    }

    pub fn stop(&self) -> Result<EngineStatus, ControllerError> {
        let mut runtime = self.runtime.lock().map_err(|_| ControllerError::Lock)?;
        if let Some(mut child) = runtime.child.take() {
            child.kill()?;
            let _ = child.wait();
            self.write_audit("engine_stopped", runtime.status.strategy_id.as_deref());
        }
        runtime.status.running = false;
        runtime.status.pid = None;
        Ok(runtime.status.clone())
    }

    fn resolve_executable(&self) -> Result<PathBuf, ControllerError> {
        let candidate = self.engine_dir.join("winws.exe");
        if !candidate.is_file() {
            return Err(ControllerError::MissingEngine(self.engine_dir.display().to_string()));
        }
        let root = self.engine_dir.canonicalize()?;
        let executable = candidate.canonicalize()?;
        if !executable.starts_with(&root)
            || executable.file_name().and_then(|name| name.to_str()) != Some("winws.exe")
        {
            return Err(ControllerError::UnsafeEnginePath);
        }
        Ok(executable)
    }

    fn expand_args(&self, strategy: &Strategy) -> Vec<String> {
        let engine = self.engine_dir.to_string_lossy();
        let lists = self.data_dir.join("lists").to_string_lossy().into_owned();
        strategy
            .args
            .iter()
            .map(|argument| argument.replace("{{engine}}", &engine).replace("{{lists}}", &lists))
            .collect()
    }

    fn write_audit(&self, event: &str, strategy_id: Option<&str>) {
        let line = format!(
            "{} event={} strategy={}\n",
            Utc::now().to_rfc3339(),
            event,
            strategy_id.unwrap_or("none")
        );
        if let Ok(mut file) = OpenOptions::new()
            .create(true)
            .append(true)
            .open(self.log_dir.join("controller.log"))
        {
            let _ = file.write_all(line.as_bytes());
        }
    }
}

fn refresh_runtime(runtime: &mut RuntimeState) -> Result<(), ControllerError> {
    if let Some(child) = runtime.child.as_mut() {
        if let Some(exit) = child.try_wait()? {
            runtime.child = None;
            runtime.status.running = false;
            runtime.status.pid = None;
            runtime.status.last_error = Some(format!("engine exited with {exit}"));
        }
    }
    Ok(())
}

impl Drop for EngineController {
    fn drop(&mut self) {
        if let Ok(runtime) = self.runtime.get_mut() {
            if let Some(child) = runtime.child.as_mut() {
                let _ = child.kill();
                let _ = child.wait();
            }
        }
    }
}

fn write_seed_if_missing(path: &PathBuf, contents: &str) -> Result<(), std::io::Error> {
    if !path.exists() {
        fs::write(path, contents)?;
    }
    Ok(())
}

