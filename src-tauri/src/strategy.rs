use serde::{Deserialize, Serialize};
use std::collections::HashSet;
use thiserror::Error;

const STRATEGIES: &str = include_str!("../../strategies/strategies.json");

#[derive(Debug, Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct StrategyDatabase {
    pub schema_version: u32,
    pub strategies: Vec<Strategy>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Strategy {
    pub id: String,
    pub name: String,
    pub description: String,
    pub targets: Vec<String>,
    pub protocols: Vec<String>,
    pub experimental: bool,
    pub args: Vec<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StrategySummary {
    pub id: String,
    pub name: String,
    pub description: String,
    pub targets: Vec<String>,
    pub protocols: Vec<String>,
    pub experimental: bool,
}

impl From<&Strategy> for StrategySummary {
    fn from(value: &Strategy) -> Self {
        Self {
            id: value.id.clone(),
            name: value.name.clone(),
            description: value.description.clone(),
            targets: value.targets.clone(),
            protocols: value.protocols.clone(),
            experimental: value.experimental,
        }
    }
}

#[derive(Debug, Error)]
pub enum StrategyError {
    #[error("strategy database is invalid: {0}")]
    Json(#[from] serde_json::Error),
    #[error("unsupported strategy schema version {0}")]
    Schema(u32),
    #[error("strategy id is invalid: {0}")]
    InvalidId(String),
    #[error("duplicate strategy id: {0}")]
    DuplicateId(String),
    #[error("strategy {0} contains an invalid argument")]
    InvalidArgument(String),
    #[error("strategy was not found: {0}")]
    NotFound(String),
}

impl StrategyDatabase {
    pub fn load_embedded() -> Result<Self, StrategyError> {
        let database: Self = serde_json::from_str(STRATEGIES)?;
        database.validate()?;
        Ok(database)
    }

    pub fn validate(&self) -> Result<(), StrategyError> {
        if self.schema_version != 1 {
            return Err(StrategyError::Schema(self.schema_version));
        }

        let mut ids = HashSet::new();
        for strategy in &self.strategies {
            if strategy.id.is_empty()
                || !strategy
                    .id
                    .bytes()
                    .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_'))
            {
                return Err(StrategyError::InvalidId(strategy.id.clone()));
            }
            if !ids.insert(strategy.id.as_str()) {
                return Err(StrategyError::DuplicateId(strategy.id.clone()));
            }
            if strategy.args.is_empty()
                || strategy
                    .args
                    .iter()
                    .any(|argument| argument.is_empty() || argument.contains('\0') || !argument.starts_with("--"))
            {
                return Err(StrategyError::InvalidArgument(strategy.id.clone()));
            }
        }
        Ok(())
    }

    pub fn get(&self, id: &str) -> Result<&Strategy, StrategyError> {
        self.strategies
            .iter()
            .find(|strategy| strategy.id == id)
            .ok_or_else(|| StrategyError::NotFound(id.to_owned()))
    }

    pub fn summaries(&self) -> Vec<StrategySummary> {
        self.strategies.iter().map(StrategySummary::from).collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn embedded_database_is_valid() {
        let database = StrategyDatabase::load_embedded().expect("valid embedded database");
        assert!(database.strategies.len() >= 3);
        assert!(database.get("adaptive-balanced").is_ok());
    }

    #[test]
    fn rejects_shell_like_argument_without_option_prefix() {
        let database = StrategyDatabase {
            schema_version: 1,
            strategies: vec![Strategy {
                id: "bad".into(),
                name: "Bad".into(),
                description: String::new(),
                targets: vec![],
                protocols: vec![],
                experimental: true,
                args: vec!["cmd.exe /c whoami".into()],
            }],
        };
        assert!(matches!(database.validate(), Err(StrategyError::InvalidArgument(_))));
    }
}

