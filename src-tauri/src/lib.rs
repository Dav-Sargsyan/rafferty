mod controller;
mod strategy;

use controller::{EngineController, EngineStatus};
use strategy::StrategySummary;
use tauri::{Manager, State};

#[tauri::command]
fn engine_status(controller: State<'_, EngineController>) -> Result<EngineStatus, String> {
    controller.status().map_err(|error| error.to_string())
}

#[tauri::command]
fn list_strategies(controller: State<'_, EngineController>) -> Vec<StrategySummary> {
    controller.list_strategies()
}

#[tauri::command]
fn start_engine(
    strategy_id: String,
    controller: State<'_, EngineController>,
) -> Result<EngineStatus, String> {
    controller.start(&strategy_id).map_err(|error| error.to_string())
}

#[tauri::command]
fn stop_engine(controller: State<'_, EngineController>) -> Result<EngineStatus, String> {
    controller.stop().map_err(|error| error.to_string())
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .setup(|app| {
            let data_dir = app.path().app_data_dir()?;
            let controller = EngineController::new(data_dir)
                .map_err(|error| Box::<dyn std::error::Error>::from(error))?;
            app.manage(controller);
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            engine_status,
            list_strategies,
            start_engine,
            stop_engine
        ])
        .run(tauri::generate_context!())
        .expect("error while running Rafferty");
}

