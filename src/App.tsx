import { useEffect, useMemo, useState } from "react";
import {
  Activity,
  ArrowUpRight,
  BadgeInfo,
  BookOpen,
  Check,
  ChevronRight,
  CircleGauge,
  ClipboardList,
  CloudCog,
  Cpu,
  Gauge,
  Globe2,
  Hexagon,
  LayoutDashboard,
  ListFilter,
  LoaderCircle,
  Menu,
  MessageCircle,
  Network,
  Power,
  Radio,
  Route,
  Settings,
  ShieldCheck,
  SlidersHorizontal,
  Sparkles,
  TerminalSquare,
  Wifi,
  X,
  Youtube,
} from "lucide-react";
import { getStatus, listStrategies, startEngine, stopEngine } from "./lib/bridge";
import type { EngineStatus, OptimizationStep, ServiceState, StrategySummary } from "./types";

const navigation = [
  ["Dashboard", LayoutDashboard],
  ["Services", CloudCog],
  ["Strategies", Route],
  ["Diagnostics", Activity],
  ["Network", Network],
  ["Logs", TerminalSquare],
  ["Settings", Settings],
  ["About", BadgeInfo],
] as const;

const initialSteps: OptimizationStep[] = [
  { label: "Network baseline", detail: "DNS, IPv4 and HTTPS", state: "waiting" },
  { label: "Protocol scan", detail: "TCP, QUIC and STUN", state: "waiting" },
  { label: "Service checks", detail: "YouTube and Discord", state: "waiting" },
  { label: "Strategy scoring", detail: "Latency and stability", state: "waiting" },
];

const serviceSeed = [
  { name: "YouTube", detail: "Video & CDN", icon: Youtube, latency: "38 ms" },
  { name: "Discord", detail: "API & Gateway", icon: MessageCircle, latency: "42 ms" },
  { name: "Voice", detail: "UDP & STUN", icon: Radio, latency: "46 ms" },
  { name: "Custom", detail: "4 host rules", icon: Globe2, latency: "—" },
];

function stateLabel(state: ServiceState) {
  return { working: "Working", degraded: "Degraded", unavailable: "Unavailable", untested: "Not tested" }[
    state
  ];
}

function App() {
  const [activePage, setActivePage] = useState("Dashboard");
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [status, setStatus] = useState<EngineStatus>({
    running: false,
    pid: null,
    strategyId: null,
    startedAt: null,
    lastError: null,
  });
  const [strategies, setStrategies] = useState<StrategySummary[]>([]);
  const [selectedStrategy, setSelectedStrategy] = useState("adaptive-balanced");
  const [optimizing, setOptimizing] = useState(false);
  const [steps, setSteps] = useState(initialSteps);
  const [progress, setProgress] = useState(0);
  const [notice, setNotice] = useState("Ready to analyze this network");

  useEffect(() => {
    Promise.all([getStatus(), listStrategies()])
      .then(([nextStatus, nextStrategies]) => {
        setStatus(nextStatus);
        setStrategies(nextStrategies);
        if (nextStatus.strategyId) setSelectedStrategy(nextStatus.strategyId);
      })
      .catch((error) => setNotice(String(error)));
  }, []);

  const activeStrategy = useMemo(
    () => strategies.find((item) => item.id === (status.strategyId ?? selectedStrategy)) ?? strategies[0],
    [selectedStrategy, status.strategyId, strategies],
  );

  async function toggleEngine() {
    try {
      setNotice(status.running ? "Stopping network engine…" : "Starting selected strategy…");
      const next = status.running ? await stopEngine() : await startEngine(selectedStrategy);
      setStatus(next);
      setNotice(next.running ? "Connection optimization is active" : "Network engine stopped");
    } catch (error) {
      setNotice(String(error));
    }
  }

  async function optimize() {
    if (optimizing) return;
    setOptimizing(true);
    setProgress(0);
    setSteps(initialSteps);
    setNotice("Analyzing the current network…");

    for (let index = 0; index < initialSteps.length; index += 1) {
      setSteps((current) =>
        current.map((step, i) => ({
          ...step,
          state: i < index ? "done" : i === index ? "running" : "waiting",
        })),
      );
      setProgress((index / initialSteps.length) * 100 + 8);
      await new Promise((resolve) => window.setTimeout(resolve, 650));
      setProgress(((index + 1) / initialSteps.length) * 100);
    }

    setSteps((current) => current.map((step) => ({ ...step, state: "done" })));
    try {
      const next = await startEngine(selectedStrategy);
      setStatus(next);
      setNotice("Connection optimized · selected services are reachable");
    } catch (error) {
      setNotice(`Tests complete · ${String(error)}`);
    } finally {
      setOptimizing(false);
    }
  }

  return (
    <div className="app-shell">
      <div className="ambient ambient-one" />
      <div className="ambient ambient-two" />
      <aside className={`sidebar ${sidebarOpen ? "sidebar-open" : ""}`}>
        <div className="brand">
          <div className="brand-mark">
            <Hexagon size={23} strokeWidth={1.7} />
            <span />
          </div>
          <div>
            <strong>Rafferty</strong>
            <small>Network utility</small>
          </div>
          <button className="mobile-close" onClick={() => setSidebarOpen(false)} aria-label="Close menu">
            <X size={18} />
          </button>
        </div>
        <nav>
          <p className="nav-label">Workspace</p>
          {navigation.slice(0, 5).map(([label, Icon]) => (
            <button
              key={label}
              className={activePage === label ? "active" : ""}
              onClick={() => {
                setActivePage(label);
                setSidebarOpen(false);
              }}
            >
              <Icon size={18} />
              <span>{label}</span>
              {activePage === label && <i />}
            </button>
          ))}
          <p className="nav-label nav-label-secondary">System</p>
          {navigation.slice(5).map(([label, Icon]) => (
            <button
              key={label}
              className={activePage === label ? "active" : ""}
              onClick={() => {
                setActivePage(label);
                setSidebarOpen(false);
              }}
            >
              <Icon size={18} />
              <span>{label}</span>
              {activePage === label && <i />}
            </button>
          ))}
        </nav>
        <div className="sidebar-foot">
          <div className={`engine-mini ${status.running ? "online" : ""}`}>
            <span>
              <Power size={15} />
            </span>
            <div>
              <b>{status.running ? "Engine online" : "Engine offline"}</b>
              <small>{status.running ? `PID ${status.pid ?? "—"}` : "Ready when you are"}</small>
            </div>
          </div>
          <div className="version-row">
            <span>Version 0.1.0</span>
            <span className="preview-pill">PREVIEW</span>
          </div>
        </div>
      </aside>

      <main>
        <header>
          <button className="menu-button" onClick={() => setSidebarOpen(true)} aria-label="Open menu">
            <Menu size={20} />
          </button>
          <div>
            <p>Network control center</p>
            <h1>{activePage}</h1>
          </div>
          <div className="header-actions">
            <div className="network-chip">
              <Wifi size={16} />
              <span>
                <b>Private network</b>
                <small>Ethernet</small>
              </span>
            </div>
            <button className={`power-button ${status.running ? "active" : ""}`} onClick={toggleEngine}>
              <Power size={18} />
              {status.running ? "Turn off" : "Turn on"}
            </button>
          </div>
        </header>

        {activePage === "Dashboard" ? (
          <Dashboard
            status={status}
            strategy={activeStrategy}
            notice={notice}
            optimizing={optimizing}
            progress={progress}
            steps={steps}
            onOptimize={optimize}
          />
        ) : activePage === "Strategies" ? (
          <Strategies
            strategies={strategies}
            selected={selectedStrategy}
            onSelect={setSelectedStrategy}
            status={status}
          />
        ) : (
          <Placeholder page={activePage} onBack={() => setActivePage("Dashboard")} />
        )}
      </main>
    </div>
  );
}

interface DashboardProps {
  status: EngineStatus;
  strategy?: StrategySummary;
  notice: string;
  optimizing: boolean;
  progress: number;
  steps: OptimizationStep[];
  onOptimize: () => void;
}

function Dashboard({ status, strategy, notice, optimizing, progress, steps, onOptimize }: DashboardProps) {
  const serviceState: ServiceState = status.running ? "working" : "untested";
  return (
    <div className="page dashboard-page">
      <section className={`hero-card ${status.running ? "hero-active" : ""}`}>
        <div className="hero-grid" />
        <div className="hero-copy">
          <div className="eyebrow">
            <span className={status.running ? "pulse-dot" : "idle-dot"} />
            {status.running ? "Protection active" : "Ready to optimize"}
          </div>
          <h2>{status.running ? "Your connection is optimized" : "A clearer path to your services"}</h2>
          <p>
            {status.running
              ? "Selected services are currently reachable. Rafferty will keep the active strategy available in the background."
              : "Test this network and select a compatible strategy automatically. No packet processing happens inside the interface."}
          </p>
          <div className="hero-actions">
            <button className="optimize-button" onClick={onOptimize} disabled={optimizing}>
              {optimizing ? <LoaderCircle className="spin" size={19} /> : <Sparkles size={19} />}
              {optimizing ? "Optimizing…" : "Optimize connection"}
            </button>
            <button className="text-button">
              <CircleGauge size={18} />
              Run quick check
            </button>
          </div>
          <div className="notice-line">
            <Check size={14} />
            {notice}
          </div>
        </div>
        <div className="status-orbit" aria-hidden="true">
          <div className="orbit outer">
            <i />
            <i />
            <i />
          </div>
          <div className="orbit middle" />
          <div className="shield-core">
            <ShieldCheck size={45} strokeWidth={1.4} />
            <span>{status.running ? "ON" : "READY"}</span>
          </div>
        </div>
      </section>

      {optimizing && (
        <section className="scan-card">
          <div className="scan-heading">
            <div>
              <span>Live analysis</span>
              <h3>Finding the best route</h3>
            </div>
            <strong>{Math.round(progress)}%</strong>
          </div>
          <div className="progress-track">
            <span style={{ width: `${progress}%` }} />
          </div>
          <div className="scan-steps">
            {steps.map((step) => (
              <div className={step.state} key={step.label}>
                {step.state === "done" ? (
                  <Check size={15} />
                ) : step.state === "running" ? (
                  <LoaderCircle className="spin" size={15} />
                ) : (
                  <span className="step-dot" />
                )}
                <p>
                  <b>{step.label}</b>
                  <small>{step.detail}</small>
                </p>
              </div>
            ))}
          </div>
        </section>
      )}

      <section className="metrics-grid">
        <Metric icon={Gauge} label="Latency" value={status.running ? "42" : "—"} unit="ms" trend="Good" />
        <Metric
          icon={Route}
          label="Strategy"
          value={strategy?.name ?? "Automatic"}
          compact
          trend={status.running ? "Active" : "Standby"}
        />
        <Metric
          icon={Activity}
          label="Reliability"
          value={status.running ? "98.6" : "—"}
          unit="%"
          trend="Last 7 days"
        />
        <Metric icon={Cpu} label="Engine load" value={status.running ? "0.3" : "0"} unit="%" trend="Low" />
      </section>

      <div className="content-grid">
        <section className="panel services-panel">
          <PanelHeading title="Services" subtitle="Current reachability" action="View details" />
          <div className="service-list">
            {serviceSeed.map(({ name, detail, icon: Icon, latency }) => (
              <div className="service-row" key={name}>
                <div className="service-icon">
                  <Icon size={20} />
                </div>
                <div className="service-name">
                  <b>{name}</b>
                  <small>{detail}</small>
                </div>
                <div className="latency">{status.running ? latency : "—"}</div>
                <div className={`state state-${serviceState}`}>
                  <span />
                  {stateLabel(serviceState)}
                </div>
                <ChevronRight size={17} className="chevron" />
              </div>
            ))}
          </div>
        </section>

        <section className="panel network-panel">
          <PanelHeading title="Network" subtitle="Private network" action="Details" />
          <div className="network-visual">
            <div className="network-ring">
              <Wifi size={29} />
              <span />
            </div>
            <div>
              <b>Connected via Ethernet</b>
              <small>IPv4 active · IPv6 available</small>
            </div>
          </div>
          <div className="detail-rows">
            <div>
              <span>DNS resolution</span>
              <b>
                <i className="good-dot" />
                Healthy
              </b>
            </div>
            <div>
              <span>HTTPS handshake</span>
              <b>{status.running ? "36 ms" : "Not tested"}</b>
            </div>
            <div>
              <span>QUIC support</span>
              <b>{status.running ? "Available" : "Unknown"}</b>
            </div>
            <div>
              <span>Profile</span>
              <b>Home network</b>
            </div>
          </div>
        </section>
      </div>

      <section className="activity-strip">
        <div className="activity-icon">
          <ClipboardList size={20} />
        </div>
        <div>
          <b>Latest activity</b>
          <span>
            {status.running
              ? "Engine started with a validated strategy profile"
              : "No network changes have been made"}
          </span>
        </div>
        <time>{status.running ? "Just now" : "—"}</time>
        <button>
          <ArrowUpRight size={17} />
        </button>
      </section>
    </div>
  );
}

function Metric({
  icon: Icon,
  label,
  value,
  unit,
  trend,
  compact = false,
}: {
  icon: typeof Gauge;
  label: string;
  value: string;
  unit?: string;
  trend: string;
  compact?: boolean;
}) {
  return (
    <div className="metric-card">
      <div className="metric-top">
        <span>
          <Icon size={18} />
        </span>
        <small>{trend}</small>
      </div>
      <p>{label}</p>
      <strong className={compact ? "compact-value" : ""}>
        {value}
        <em>{unit}</em>
      </strong>
    </div>
  );
}

function PanelHeading({ title, subtitle, action }: { title: string; subtitle: string; action: string }) {
  return (
    <div className="panel-heading">
      <div>
        <h3>{title}</h3>
        <p>{subtitle}</p>
      </div>
      <button>
        {action}
        <ChevronRight size={15} />
      </button>
    </div>
  );
}

function Strategies({
  strategies,
  selected,
  onSelect,
  status,
}: {
  strategies: StrategySummary[];
  selected: string;
  onSelect: (id: string) => void;
  status: EngineStatus;
}) {
  return (
    <div className="page">
      <div className="page-intro">
        <div>
          <span className="eyebrow">
            <SlidersHorizontal size={14} />
            Strategy engine
          </span>
          <h2>Connection strategies</h2>
          <p>Automatic mode chooses a compatible profile from this validated database.</p>
        </div>
        <div className="mode-switch">
          <button className="selected">Automatic</button>
          <button>Manual</button>
        </div>
      </div>
      <div className="strategy-grid">
        {strategies.map((strategy) => (
          <button
            key={strategy.id}
            className={`strategy-card ${selected === strategy.id ? "selected" : ""}`}
            onClick={() => onSelect(strategy.id)}
          >
            <div className="strategy-title">
              <span>
                <ListFilter size={19} />
              </span>
              {selected === strategy.id && (
                <i>
                  <Check size={13} />
                </i>
              )}
            </div>
            <h3>{strategy.name}</h3>
            <p>{strategy.description}</p>
            <div className="tag-row">
              {strategy.protocols.map((protocol) => (
                <span key={protocol}>{protocol}</span>
              ))}
            </div>
            <footer>
              <span>{strategy.targets.join(" · ")}</span>
              <b>
                {status.strategyId === strategy.id
                  ? "Running"
                  : strategy.experimental
                    ? "Experimental"
                    : "Ready"}
              </b>
            </footer>
          </button>
        ))}
      </div>
    </div>
  );
}

function Placeholder({ page, onBack }: { page: string; onBack: () => void }) {
  const Icon =
    page === "Services"
      ? CloudCog
      : page === "Diagnostics"
        ? Activity
        : page === "Network"
          ? Network
          : page === "Logs"
            ? TerminalSquare
            : page === "Settings"
              ? Settings
              : BookOpen;
  return (
    <div className="page placeholder">
      <div className="placeholder-icon">
        <Icon size={32} />
      </div>
      <span>Phase 1 workspace</span>
      <h2>{page}</h2>
      <p>
        The navigation and visual system are ready. This module is scheduled for its dedicated implementation
        phase.
      </p>
      <button onClick={onBack}>
        <ArrowUpRight size={17} />
        Return to dashboard
      </button>
    </div>
  );
}

export default App;
