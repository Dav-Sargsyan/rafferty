import { describe, expect, it } from "vitest";
import strategies from "../strategies/strategies.json";
import defaults from "../config/default.json";

describe("strategy database", () => {
  it("uses schema version 1 and unique safe ids", () => {
    expect(strategies.schema_version).toBe(1);
    const ids = strategies.strategies.map((strategy) => strategy.id);
    expect(new Set(ids).size).toBe(ids.length);
    expect(ids.every((id) => /^[a-z0-9_-]+$/i.test(id))).toBe(true);
  });

  it("stores process arguments as option arrays", () => {
    for (const strategy of strategies.strategies) {
      expect(strategy.args.length).toBeGreaterThan(0);
      expect(strategy.args.every((argument) => argument.startsWith("--"))).toBe(true);
    }
  });
});

describe("default configuration", () => {
  it("uses conservative background behavior", () => {
    expect(defaults.mode).toBe("automatic");
    expect(defaults.start_with_windows).toBe(false);
    expect(defaults.health_check_minutes).toBeGreaterThanOrEqual(5);
    expect(defaults.advanced_mode).toBe(false);
  });
});
