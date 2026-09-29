import { invoke } from "@tauri-apps/api/core";
import type { EngineStatus, StrategySummary } from "../types";

const demoStrategies: StrategySummary[] = [
  {
    id: "adaptive-balanced",
    name: "Adaptive Balanced",
    description: "Balanced TCP and QUIC handling for everyday networks.",
    targets: ["YouTube", "Discord", "Voice"],
    protocols: ["TCP", "UDP", "QUIC"],
    experimental: false,
  },
  {
    id: "tcp-split",
    name: "TLS Split",
    description: "Focused TCP profile for networks where QUIC is already reachable.",
    targets: ["YouTube", "Discord"],
    protocols: ["TCP"],
    experimental: false,
  },
  {
    id: "voice-first",
    name: "Voice First",
    description: "Prioritizes Discord voice and STUN reachability.",
    targets: ["Discord", "Voice"],
    protocols: ["UDP", "STUN"],
    experimental: true,
  },
];

let demoStatus: EngineStatus = {
  running: false,
  pid: null,
  strategyId: null,
  startedAt: null,
  lastError: null,
};

function hasTauriRuntime() {
  return "__TAURI_INTERNALS__" in window;
}

export async function getStatus(): Promise<EngineStatus> {
  if (!hasTauriRuntime()) return demoStatus;
  return invoke<EngineStatus>("engine_status");
}

export async function listStrategies(): Promise<StrategySummary[]> {
  if (!hasTauriRuntime()) return demoStrategies;
  return invoke<StrategySummary[]>("list_strategies");
}

export async function startEngine(strategyId: string): Promise<EngineStatus> {
  if (!hasTauriRuntime()) {
    demoStatus = {
      running: true,
      pid: 4820,
      strategyId,
      startedAt: new Date().toISOString(),
      lastError: null,
    };
    return demoStatus;
  }
  return invoke<EngineStatus>("start_engine", { strategyId });
}

export async function stopEngine(): Promise<EngineStatus> {
  if (!hasTauriRuntime()) {
    demoStatus = { ...demoStatus, running: false, pid: null };
    return demoStatus;
  }
  return invoke<EngineStatus>("stop_engine");
}
