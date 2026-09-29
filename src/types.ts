export type ServiceState = "working" | "degraded" | "unavailable" | "untested";

export interface EngineStatus {
  running: boolean;
  pid: number | null;
  strategyId: string | null;
  startedAt: string | null;
  lastError: string | null;
}

export interface StrategySummary {
  id: string;
  name: string;
  description: string;
  targets: string[];
  protocols: string[];
  experimental: boolean;
}

export interface OptimizationStep {
  label: string;
  detail: string;
  state: "waiting" | "running" | "done";
}
