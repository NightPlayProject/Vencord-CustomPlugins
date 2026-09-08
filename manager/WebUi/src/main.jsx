import React, { useEffect, useMemo, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import {
  Activity,
  BadgeCheck,
  Check,
  ChevronRight,
  CircleDot,
  Clipboard,
  CloudDownload,
  Code2,
  ExternalLink,
  FolderOpen,
  Github,
  HardDriveDownload,
  HeartPulse,
  PackageCheck,
  RefreshCw,
  RotateCcw,
  Server,
  Settings2,
  ShieldCheck,
  Sparkles,
  Trash2,
  TriangleAlert,
  Wrench,
  X,
  Zap,
} from "lucide-react";
import "./styles.css";

const initialState = {
  managerVersion: "0.1.7",
  statusText: "Loading manager state…",
  statusTone: "accent",
  selectedBranch: "stable",
  selectedChannelLabel: "Stable channel",
  installedVersion: "—",
  latestVersion: "—",
  components: {
    vencord: "—",
    orion: "—",
    nitro: "—",
    loader: "—",
  },
  clients: {
    stable: { label: "Discord Stable", status: "Checking…", tone: "neutral", selected: true, found: true },
    ptb: { label: "Discord PTB", status: "Checking…", tone: "neutral", selected: false, found: true },
    canary: { label: "Discord Canary", status: "Checking…", tone: "neutral", selected: false, found: true },
  },
  actions: {
    primary: { label: "Loading…", enabled: false },
    check: { enabled: false },
    repair: { enabled: false },
    uninstall: { enabled: false },
    plugins: { enabled: true },
    installFolder: { enabled: false },
    managerUpdate: { visible: false, label: "Update manager", enabled: false },
  },
  progress: { message: "Starting…", value: 0, indeterminate: true },
  busy: true,
  log: "",
};

const toneMap = {
  success: {
    chip: "border-emerald-400/20 bg-emerald-400/10 text-emerald-300",
    dot: "bg-emerald-400",
    glow: "shadow-[0_0_28px_rgba(52,211,153,.18)]",
  },
  warning: {
    chip: "border-amber-400/20 bg-amber-400/10 text-amber-200",
    dot: "bg-amber-400",
    glow: "shadow-[0_0_28px_rgba(251,191,36,.14)]",
  },
  danger: {
    chip: "border-rose-400/20 bg-rose-400/10 text-rose-200",
    dot: "bg-rose-400",
    glow: "shadow-[0_0_28px_rgba(251,113,133,.16)]",
  },
  accent: {
    chip: "border-white/[0.12] bg-white/[0.045] text-zinc-200",
    dot: "bg-zinc-300",
    glow: "shadow-[0_0_24px_rgba(255,255,255,.06)]",
  },
  neutral: {
    chip: "border-white/[0.09] bg-white/[0.025] text-zinc-400",
    dot: "bg-zinc-600",
    glow: "",
  },
};

const clientMeta = {
  stable: { short: "Stable", accent: "from-white/[0.045] to-transparent" },
  ptb: { short: "PTB", accent: "from-white/[0.04] to-transparent" },
  canary: { short: "Canary", accent: "from-white/[0.035] to-transparent" },
};

function nativePost(message) {
  try {
    window.chrome?.webview?.postMessage(message);
  } catch {
    // Browser-only development preview has no native bridge.
  }
}

function cx(...parts) {
  return parts.filter(Boolean).join(" ");
}

function TinyStatus({ tone = "neutral", children, pulse = false }) {
  const style = toneMap[tone] ?? toneMap.neutral;
  return (
    <span className={cx("inline-flex max-w-full items-center gap-2 rounded-full border px-3 py-1.5 text-[11px] font-semibold tracking-[.01em]", style.chip)}>
      <span className="relative flex h-2 w-2">
        {pulse && <span className={cx("absolute inline-flex h-full w-full animate-ping rounded-full opacity-50", style.dot)} />}
        <span className={cx("relative inline-flex h-2 w-2 rounded-full", style.dot)} />
      </span>
      <span className="min-w-0 break-words leading-4">{children}</span>
    </span>
  );
}

function IconButton({ icon: Icon, label, onClick, disabled = false, danger = false }) {
  return (
    <button
      type="button"
      disabled={disabled}
      onClick={onClick}
      className={cx(
        "group flex min-h-11 items-center gap-3 rounded-xl border px-3.5 text-left text-[12px] font-semibold transition-all duration-150 max-[1100px]:gap-2.5 max-[1100px]:px-3",
        danger
          ? "border-rose-400/16 bg-rose-400/[0.055] text-rose-200 hover:border-rose-400/30 hover:bg-rose-400/[0.09]"
          : "border-white/[0.08] bg-[#0b0b0b] text-zinc-200 hover:-translate-y-px hover:border-white/[0.15] hover:bg-[#111111]",
        disabled && "cursor-not-allowed opacity-35 hover:translate-y-0"
      )}
    >
      <span className={cx("grid h-8 w-8 place-items-center rounded-lg border", danger ? "border-rose-400/15 bg-rose-400/10" : "border-white/[0.07] bg-[#070707]")}>
        <Icon size={15} strokeWidth={1.9} />
      </span>
      <span className="min-w-0 flex-1 break-words leading-4">{label}</span>
      <ChevronRight size={14} className="opacity-25 transition-transform group-hover:translate-x-0.5 group-hover:opacity-60 max-[1100px]:hidden" />
    </button>
  );
}

function ClientCard({ branch, client, disabled, onSelect }) {
  const selected = Boolean(client?.selected);
  const tone = client?.tone || "neutral";
  const style = toneMap[tone] ?? toneMap.neutral;
  const meta = clientMeta[branch];
  return (
    <button
      id={`client-${branch}`}
      type="button"
      aria-pressed={selected}
      disabled={disabled}
      onClick={() => onSelect(branch)}
      className={cx(
        "group relative h-[68px] w-full min-w-0 overflow-hidden rounded-[15px] border px-2.5 text-left transition-[border-color,background-color,box-shadow,transform] duration-150 active:scale-[.996]",
        selected
          ? "border-white/[0.18] bg-white/[0.055] shadow-[inset_0_0_0_1px_rgba(255,255,255,.025),0_10px_28px_rgba(0,0,0,.24)]"
          : "border-transparent bg-transparent hover:border-white/[0.075] hover:bg-white/[0.025]",
        disabled && "cursor-not-allowed opacity-45"
      )}
    >
      <div className={cx("pointer-events-none absolute inset-0 bg-gradient-to-r opacity-55 transition-opacity duration-150", meta.accent, selected ? "opacity-80" : "opacity-25 group-hover:opacity-45")} />
      <span className={cx("pointer-events-none absolute bottom-3 left-0 top-3 w-[2px] rounded-r-full bg-white/75 transition-opacity duration-150", selected ? "opacity-100" : "opacity-0")} />

      <div className="relative grid h-full min-w-0 grid-cols-[36px_minmax(0,1fr)] items-center gap-2.5">
        <div className={cx(
          "grid h-9 w-9 shrink-0 place-items-center rounded-xl border border-white/[0.07] bg-[#070707] text-zinc-500 transition-colors duration-150",
          selected ? "border-white/[0.14] bg-white/[0.055] text-white" : "group-hover:text-zinc-200"
        )}>
          <Server size={16.5} strokeWidth={1.8} />
        </div>

        <div className="min-w-0 flex-1">
          <div className="flex min-w-0 items-center justify-between gap-2">
            <span className="truncate text-[12.3px] font-bold leading-4 text-white">{meta.short}</span>
            <span className={cx(
              "grid h-[20px] w-[20px] shrink-0 place-items-center rounded-full border transition-all duration-150",
              selected
                ? "border-white/[0.18] bg-white/[0.07] text-white opacity-100"
                : "border-transparent bg-transparent text-transparent opacity-0"
            )}>
              <Check size={10.5} strokeWidth={2.5} />
            </span>
          </div>
          <div className="mt-0.5 flex min-w-0 items-center gap-1.5 text-[9.4px] leading-4 tracking-[-.01em] text-zinc-500">
            <span className={cx("h-1.5 w-1.5 shrink-0 rounded-full", style.dot)} />
            <span className="truncate">{client?.status || "Checking…"}</span>
          </div>
        </div>
      </div>
    </button>
  );
}

function ComponentRow({ icon: Icon, name, value, accent = false }) {
  return (
    <div className="flex min-h-[54px] items-center gap-3 rounded-xl border border-white/[0.065] bg-[#0b0b0b] px-3.5 py-2.5">
      <span className={cx("grid h-8 w-8 shrink-0 place-items-center rounded-lg border border-white/[0.07] bg-[#070707]", accent && "border-emerald-400/10 bg-emerald-400/[0.06] text-emerald-300")}>
        <Icon size={14.5} strokeWidth={1.8} />
      </span>
      <span className="min-w-0 flex-1 break-words pr-2 text-[11.5px] font-medium leading-4 text-zinc-300">{name}</span>
      <span className={cx("shrink-0 whitespace-nowrap rounded-lg border px-2.5 py-1 text-[10.5px] font-bold", accent ? "border-emerald-400/15 bg-emerald-400/[0.08] text-emerald-300" : "border-white/[0.07] bg-black/25 text-slate-100")}>
        {value || "—"}
      </span>
    </div>
  );
}

function SkeletonBlock({ className = "" }) {
  return <span aria-hidden="true" className={cx("skeleton-block block", className)} />;
}

function DashboardSkeleton() {
  return (
    <main aria-busy="true" aria-label="Loading dashboard" className="relative h-full w-full overflow-hidden bg-[#050505] text-zinc-100">
      <div className="pointer-events-none absolute inset-0 grid-overlay opacity-55" />
      <div className="manager-shell relative grid h-full grid-cols-[226px_minmax(0,1fr)] max-[1100px]:grid-cols-[214px_minmax(0,1fr)] max-[980px]:grid-cols-[204px_minmax(0,1fr)]">
        <aside className="manager-sidebar min-w-0 border-r border-white/[0.07] bg-[#070707]/95 px-[18px] py-5 max-[1100px]:px-4">
          <div className="flex h-full flex-col">
            <div className="flex items-center gap-3 px-1 pt-1">
              <SkeletonBlock className="h-10 w-10 shrink-0 rounded-2xl" />
              <div className="min-w-0 flex-1">
                <SkeletonBlock className="h-3 w-[104px] rounded-md" />
                <SkeletonBlock className="mt-2 h-2 w-[68px] rounded-md" />
              </div>
            </div>

            <div className="mt-8 flex items-center gap-2 px-1">
              <SkeletonBlock className="h-2 w-[76px] rounded-md" />
              <span className="h-px flex-1 bg-white/[0.05]" />
            </div>
            <div className="mt-2.5 grid gap-1.5 rounded-[16px] border border-white/[0.07] bg-[#050505] p-1">
              {[0, 1, 2].map(index => (
                <div key={index} className="grid h-[68px] grid-cols-[36px_minmax(0,1fr)] items-center gap-2.5 rounded-[15px] px-2.5">
                  <SkeletonBlock className="h-9 w-9 rounded-xl" />
                  <div className="min-w-0">
                    <SkeletonBlock className="h-2.5 w-[58px] rounded-md" />
                    <SkeletonBlock className="mt-2 h-2 w-[88px] max-w-full rounded-md" />
                  </div>
                </div>
              ))}
            </div>

            <div className="mt-auto rounded-2xl border border-white/[0.07] bg-[#080808] p-3.5">
              <SkeletonBlock className="h-2.5 w-[96px] rounded-md" />
              <SkeletonBlock className="mt-2.5 h-2 w-full rounded-md" />
              <SkeletonBlock className="mt-2 h-2 w-[82%] rounded-md" />
            </div>
          </div>
        </aside>

        <section className="manager-scroll min-w-0 overflow-y-auto px-5 pb-6 pt-5 max-[1100px]:px-4">
          <header className="flex min-w-0 items-start justify-between gap-5">
            <div className="min-w-0 flex-1">
              <SkeletonBlock className="h-2 w-[130px] rounded-md" />
              <SkeletonBlock className="mt-4 h-6 w-[260px] max-w-[70%] rounded-lg" />
              <SkeletonBlock className="mt-3 h-2.5 w-[285px] max-w-[78%] rounded-md" />
            </div>
            <SkeletonBlock className="h-10 w-[106px] shrink-0 rounded-xl" />
          </header>

          <section className="glass-card mt-5 overflow-hidden rounded-[20px] p-5">
            <div className="flex min-w-0 items-start justify-between gap-6">
              <div className="min-w-0 flex-1">
                <SkeletonBlock className="h-[29px] w-[150px] rounded-full" />
                <SkeletonBlock className="mt-5 h-6 w-[390px] max-w-[82%] rounded-lg" />
                <SkeletonBlock className="mt-3 h-2.5 w-[425px] max-w-[92%] rounded-md" />
              </div>
              <SkeletonBlock className="h-[58px] w-[126px] shrink-0 rounded-2xl" />
            </div>

            <div className="mt-6 min-w-0 rounded-[16px] border border-white/[0.07] bg-[#080808] p-[18px]">
              <div className="grid grid-cols-[1fr_auto_1fr] items-end gap-4">
                <div>
                  <SkeletonBlock className="h-2 w-[62px] rounded-md" />
                  <SkeletonBlock className="mt-3 h-[30px] w-[116px] rounded-lg" />
                </div>
                <SkeletonBlock className="mb-2 h-4 w-4 rounded-md" />
                <div>
                  <SkeletonBlock className="h-2 w-[88px] rounded-md" />
                  <SkeletonBlock className="mt-3 h-[30px] w-[116px] rounded-lg" />
                </div>
              </div>
              <div className="mt-5 grid grid-cols-3 gap-2.5">
                {[0, 1, 2].map(index => <SkeletonBlock key={index} className="h-14 w-full rounded-xl" />)}
              </div>
            </div>

            <div className="mt-3.5 grid grid-cols-2 gap-2.5">
              <SkeletonBlock className="h-[52px] w-full rounded-xl" />
              <SkeletonBlock className="h-[52px] w-full rounded-xl" />
            </div>
            <div className="mt-5 flex items-center justify-between">
              <SkeletonBlock className="h-2 w-[180px] rounded-md" />
              <SkeletonBlock className="h-2 w-7 rounded-md" />
            </div>
            <SkeletonBlock className="mt-2 h-1.5 w-full rounded-full" />
          </section>

          <div className="mt-4 grid min-w-0 grid-cols-[1.08fr_.92fr] gap-4 max-[980px]:grid-cols-1">
            {[0, 1].map(card => (
              <section key={card} className="glass-card min-h-[260px] rounded-[18px] p-5">
                <div className="flex items-center justify-between gap-3">
                  <SkeletonBlock className="h-3 w-[130px] rounded-md" />
                  <SkeletonBlock className="h-7 w-[64px] rounded-full" />
                </div>
                <SkeletonBlock className="mt-3 h-2 w-[190px] max-w-[70%] rounded-md" />
                <div className="mt-4 grid grid-cols-2 gap-2.5">
                  {[0, 1, 2, 3].map(row => <SkeletonBlock key={row} className="h-[54px] w-full rounded-xl" />)}
                </div>
              </section>
            ))}
          </div>

          <section className="glass-card mt-4 rounded-[18px] p-5">
            <div className="flex items-center justify-between">
              <SkeletonBlock className="h-3 w-[110px] rounded-md" />
              <div className="flex gap-2"><SkeletonBlock className="h-9 w-9 rounded-lg" /><SkeletonBlock className="h-9 w-20 rounded-lg" /></div>
            </div>
            <SkeletonBlock className="mt-4 h-[132px] w-full rounded-2xl" />
          </section>
        </section>
      </div>
    </main>
  );
}

function ConfirmModal({ dialog, onResult }) {
  const cancelRef = useRef(null);
  const confirmRef = useRef(null);
  useEffect(() => {
    if (!dialog) return;
    const target = dialog.tone === "danger" && dialog.showCancel ? cancelRef.current : confirmRef.current;
    setTimeout(() => target?.focus(), 0);

    const handleKeyDown = event => {
      if (event.key !== "Escape") return;
      event.preventDefault();
      onResult(false);
    };
    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [dialog]);

  if (!dialog) return null;
  const tone = dialog.tone || "accent";
  const danger = tone === "danger";
  const warning = tone === "warning";
  const success = tone === "success";
  return (
    <div
      className="modal-backdrop-enter absolute inset-0 z-50 grid place-items-center bg-black/85 p-7 backdrop-blur-md"
    >
      <div
        className="modal-panel-enter glass-card w-full max-w-[470px] rounded-[24px] p-5 shadow-[0_35px_110px_rgba(0,0,0,.55)]"
      >
        <div className={cx(
          "grid h-11 w-11 place-items-center rounded-2xl border",
          danger ? "border-rose-400/20 bg-rose-400/10 text-rose-300" : warning ? "border-amber-400/20 bg-amber-400/10 text-amber-200" : success ? "border-emerald-400/20 bg-emerald-400/10 text-emerald-300" : "border-indigo-400/20 bg-indigo-400/10 text-indigo-200"
        )}>
          {danger || warning ? <TriangleAlert size={19} /> : success ? <Check size={20} /> : <Sparkles size={19} />}
        </div>
        <h2 className="mt-4 text-[18px] font-bold tracking-[-.02em] text-white">{dialog.title}</h2>
        <div className="manager-scroll mt-2 max-h-[230px] overflow-y-auto pr-2 text-[12.2px] leading-5 text-slate-400">
          {dialog.message}
        </div>
        <div className="mt-5 grid grid-cols-2 gap-2.5">
          {dialog.showCancel ? (
            <button ref={cancelRef} type="button" onClick={() => onResult(false)} className="min-h-11 rounded-xl border border-white/[0.09] bg-white/[0.03] text-[12px] font-bold text-slate-200 transition-colors hover:bg-white/[0.06]">
              Cancel
            </button>
          ) : <span />}
          <button
            ref={confirmRef}
            type="button"
            onClick={() => onResult(true)}
            className={cx(
              "min-h-11 rounded-xl border text-[12px] font-bold transition-all active:scale-[.99]",
              danger
                ? "border-rose-400/25 bg-rose-500/16 text-rose-100 hover:bg-rose-500/22"
                : "border-white/80 bg-[#f4f4f5] text-[#090909] shadow-[0_8px_30px_rgba(0,0,0,.24)] hover:bg-white"
            )}
          >
            {dialog.confirmText}
          </button>
        </div>
      </div>
    </div>
  );
}

function App() {
  const bootstrapState = window.__MANAGER_BOOTSTRAP__ || null;
  const [state, setState] = useState(() => bootstrapState || initialState);
  const [dialog, setDialog] = useState(null);
  const [logExpanded, setLogExpanded] = useState(true);
  const [hasNativeState, setHasNativeState] = useState(() => Boolean(bootstrapState) || !window.chrome?.webview);
  const logRef = useRef(null);

  useEffect(() => {
    const webview = window.chrome?.webview;
    if (!webview) {
      setState(prev => ({ ...prev, busy: false, statusText: "Browser preview · native bridge unavailable", progress: { message: "Preview mode", value: 100, indeterminate: false } }));
      return;
    }
    const handler = event => {
      const message = event.data;
      if (!message || typeof message !== "object") return;
      if (message.type === "state") {
        setState(message.data);
        setHasNativeState(true);
      }
      if (message.type === "dialog") setDialog(message);
      if (message.type === "dialogClosed") setDialog(current => !current || current.id === message.id ? null : current);
    };
    webview.addEventListener("message", handler);
    nativePost({ type: "ready" });
    return () => webview.removeEventListener("message", handler);
  }, []);

  useEffect(() => {
    if (!hasNativeState) return;
    const frame = requestAnimationFrame(() => nativePost({ type: "rendered" }));
    return () => cancelAnimationFrame(frame);
  }, [hasNativeState]);

  useEffect(() => {
    if (!logExpanded) return;
    const node = logRef.current;
    if (node) node.scrollTop = node.scrollHeight;
  }, [state.log, logExpanded]);

  const statusTone = state.statusTone || "accent";
  const statusStyle = toneMap[statusTone] ?? toneMap.accent;
  const clients = state.clients || initialState.clients;
  const actions = state.actions || initialState.actions;
  const progress = state.progress || initialState.progress;
  const percent = progress.indeterminate ? 42 : Math.max(0, Math.min(100, progress.value || 0));
  const selectedName = clientMeta[state.selectedBranch]?.short || state.selectedChannelLabel || "Discord";
  const installed = state.installedVersion || "—";
  const latest = state.latestVersion || "—";
  const upToDate = installed !== "—" && latest !== "—" && installed === latest;

  const primaryIcon = useMemo(() => {
    const label = actions.primary?.label?.toLowerCase() || "";
    if (label.includes("update")) return CloudDownload;
    if (label.includes("install")) return HardDriveDownload;
    if (label.includes("repair")) return Wrench;
    return Check;
  }, [actions.primary?.label]);
  const PrimaryIcon = primaryIcon;

  const action = name => nativePost({ type: "action", action: name });
  const selectBranch = branch => nativePost({ type: "selectBranch", branch });
  const resolveDialog = result => {
    if (!dialog) return;
    nativePost({ type: "dialogResult", id: dialog.id, result });
    setDialog(null);
  };

  if (window.chrome?.webview && !hasNativeState) {
    return <DashboardSkeleton />;
  }

  return (
    <main className="relative h-full w-full overflow-hidden text-zinc-100 selection:bg-white/15">
      <div className="pointer-events-none absolute inset-0 grid-overlay opacity-70" />
      <div className="pointer-events-none absolute left-[18%] top-[-220px] h-[420px] w-[620px] rounded-full bg-white/[0.022] blur-[100px]" />

      <div className="manager-shell relative grid h-full grid-cols-[226px_minmax(0,1fr)] max-[1100px]:grid-cols-[214px_minmax(0,1fr)] max-[980px]:grid-cols-[204px_minmax(0,1fr)]">
        <aside className="manager-sidebar min-w-0 border-r border-white/[0.07] bg-[#070707]/95 px-[18px] py-5 backdrop-blur-xl max-[1100px]:px-4">
          <div className="flex h-full flex-col">
            <div className="flex items-center gap-3 px-1 pt-1">
              <div className="relative grid h-10 w-10 place-items-center overflow-hidden rounded-2xl border border-white/[0.11] bg-[#111111] shadow-[0_12px_36px_rgba(0,0,0,.28)]">
                <div className="absolute inset-0 bg-[radial-gradient(circle_at_25%_20%,rgba(255,255,255,.10),transparent_34%)]" />
                <Zap size={18} className="relative text-white" fill="currentColor" strokeWidth={1.7} />
              </div>
              <div>
                <div className="text-[12.5px] font-extrabold tracking-[-.015em] text-white">Custom Vencord</div>
                <div className="mt-0.5 text-[9.5px] font-semibold uppercase tracking-[.16em] text-slate-500">manager {state.managerVersion}</div>
              </div>
            </div>

            <div id="discord-target-label" className="mt-8 flex items-center justify-between gap-2 px-1 text-[9px] font-bold uppercase tracking-[.18em] text-zinc-600">
              <span>Discord target</span>
              <span className="h-px min-w-4 flex-1 bg-gradient-to-r from-white/[0.06] to-transparent" />
            </div>
            <div role="group" aria-labelledby="discord-target-label" className="mt-2.5 grid gap-1.5 rounded-[16px] border border-white/[0.075] bg-[#050505] p-1 shadow-[inset_0_1px_0_rgba(255,255,255,.018),0_16px_36px_rgba(0,0,0,.18)]">
              {["stable", "ptb", "canary"].map(branch => (
                <ClientCard key={branch} branch={branch} client={clients[branch]} disabled={state.busy} onSelect={selectBranch} />
              ))}
            </div>

            <div className="mt-auto rounded-2xl border border-white/[0.07] bg-[#090909] p-3.5">
              <div className="flex items-center gap-2 text-[10.5px] font-semibold text-zinc-400">
                <ShieldCheck size={14.5} className="text-emerald-400" />
                Verified delivery
              </div>
              <p className="mt-1.5 text-[9.7px] leading-4 text-zinc-600">SHA-256 checks, isolated client payloads, rollback journals.</p>
            </div>
          </div>
        </aside>

        <section className="manager-content manager-scroll min-w-0 h-full overflow-y-auto px-6 pb-8 pt-5 max-[1100px]:px-4 max-[980px]:pb-6">
          <header className="manager-header flex min-w-0 items-center justify-between gap-6 max-[980px]:flex-wrap max-[980px]:gap-3">
            <div className="min-w-0">
              <div className="flex items-center gap-2 text-[9.5px] font-bold uppercase tracking-[.18em] text-zinc-500">
                <Sparkles size={12.5} /> Release control center
              </div>
              <h1 className="mt-1.5 text-[26px] font-bold tracking-[-.035em] text-white">Keep every client sharp.</h1>
              <p className="mt-1.5 text-[11.5px] leading-5 text-zinc-500">One verified manager for Stable, PTB, and Canary.</p>
            </div>
            <div className="manager-header-actions flex shrink-0 items-center gap-2.5 max-[980px]:ml-auto">
              {actions.managerUpdate?.visible && (
                <button disabled={!actions.managerUpdate.enabled} onClick={() => action("managerUpdate")} className="rounded-xl border border-white/[0.12] bg-white/[0.05] px-3.5 py-2.5 text-[10.8px] font-bold text-zinc-100 transition-colors hover:bg-white/[0.08] disabled:opacity-40">
                  {actions.managerUpdate.label}
                </button>
              )}
              <button onClick={() => action("viewRelease")} className="flex h-10 items-center gap-2 rounded-xl border border-white/[0.09] bg-[#0b0b0b] px-3.5 text-[10.8px] font-bold text-zinc-300 transition-colors hover:border-white/[0.15] hover:bg-[#111111] hover:text-white">
                <Github size={14.5} /> Releases <ExternalLink size={12.5} className="opacity-50" />
              </button>
            </div>
          </header>

          <section className="glass-card relative mt-5 overflow-hidden rounded-[20px] p-6 max-[1100px]:p-5">
              <div className={cx("pointer-events-none absolute -right-20 -top-24 h-64 w-64 rounded-full blur-[80px]", statusTone === "success" ? "bg-emerald-400/[0.045]" : statusTone === "warning" ? "bg-amber-400/[0.045]" : statusTone === "danger" ? "bg-rose-400/[0.045]" : "bg-white/[0.025]")} />
              <div className="release-heading relative flex min-w-0 items-start justify-between gap-6 max-[980px]:gap-3">
                <div className="min-w-0 flex-1">
                  <TinyStatus tone={statusTone} pulse={state.busy}>{state.statusText}</TinyStatus>
                  <h2 className="mt-5 max-w-[620px] text-[22px] font-bold tracking-[-.025em] text-white">
                    {upToDate ? `${selectedName} is fully current.` : `A verified release is ready for ${selectedName}.`}
                  </h2>
                  <p className="mt-2 max-w-[620px] text-[11.2px] leading-5 text-zinc-500">
                    The manager validates the package before touching Discord and keeps every client on its own payload.
                  </p>
                </div>
                <div className="shrink-0 rounded-2xl border border-white/[0.08] bg-[#090909] px-4 py-3 text-right">
                  <div className="text-[9px] font-bold uppercase tracking-[.16em] text-zinc-600">Target</div>
                  <div className="mt-1.5 whitespace-nowrap text-[11.5px] font-bold text-zinc-200">{state.selectedChannelLabel}</div>
                </div>
              </div>

              <div className="release-detail-grid relative mt-6 grid grid-cols-[minmax(0,1fr)_240px] gap-5 max-[1140px]:grid-cols-1 max-[1140px]:gap-3.5">
                <div className="inner-surface min-w-0 rounded-[16px] p-[18px]">
                  <div className="version-grid grid grid-cols-[minmax(0,1fr)_auto_minmax(0,1fr)] items-end gap-4">
                    <div className="min-w-0">
                      <div className="text-[9px] font-bold uppercase tracking-[.16em] text-zinc-600">Installed</div>
                      <div className="mt-1.5 truncate text-[30px] font-extrabold tracking-[-.05em] text-white">{installed}</div>
                    </div>
                    <div className="pb-2.5 text-zinc-700"><ChevronRight size={18} /></div>
                    <div className="min-w-0">
                      <div className="whitespace-nowrap text-[9px] font-bold uppercase tracking-[.16em] text-zinc-600">Latest verified</div>
                      <div className="mt-1.5 truncate text-[30px] font-extrabold tracking-[-.05em] text-white">{latest}</div>
                    </div>
                  </div>
                  <div className="trust-grid mt-5 grid grid-cols-3 gap-2.5">
                    {[
                      [BadgeCheck, "SHA-256", "Verified"],
                      [RotateCcw, "Rollback", "Ready"],
                      [ShieldCheck, "Isolation", "Per-client"],
                    ].map(([Icon, title, value]) => (
                      <div key={title} className="flex min-w-0 items-center gap-2.5 rounded-xl border border-white/[0.065] bg-[#0b0b0b] p-3">
                        <span className="grid h-8 w-8 shrink-0 place-items-center rounded-lg border border-white/[0.07] bg-[#070707]">
                          <Icon size={14} className={title === "SHA-256" ? "text-emerald-400" : "text-zinc-400"} />
                        </span>
                        <div className="min-w-0">
                          <div className="truncate text-[8.6px] font-bold uppercase tracking-[.11em] text-zinc-600">{title}</div>
                          <div className="mt-0.5 truncate text-[10.6px] font-semibold text-zinc-300">{value}</div>
                        </div>
                      </div>
                    ))}
                  </div>
                </div>

                <div className="release-actions grid min-w-0 grid-cols-2 gap-2.5 min-[1141px]:flex min-[1141px]:flex-col min-[1141px]:justify-end">
                  <button
                    type="button"
                    disabled={!actions.primary?.enabled}
                    onClick={() => action("primary")}
                    className="group flex min-h-[52px] items-center justify-center gap-2.5 rounded-xl border border-white/80 bg-[#f4f4f5] px-5 text-center text-[11.8px] font-extrabold leading-4 text-[#080808] shadow-[0_12px_34px_rgba(0,0,0,.28)] transition-all hover:bg-white active:scale-[.992] disabled:cursor-not-allowed disabled:border-white/[0.07] disabled:bg-[#111111] disabled:text-zinc-600 disabled:shadow-none"
                  >
                    <PrimaryIcon size={15.5} strokeWidth={2.2} className="shrink-0" />
                    <span>{actions.primary?.label || "Continue"}</span>
                  </button>
                  <button type="button" disabled={!actions.check?.enabled} onClick={() => action("check")} className="flex min-h-11 items-center justify-center gap-2 rounded-xl border border-white/[0.08] bg-[#090909] px-4 text-[10.8px] font-bold text-zinc-400 transition-colors hover:border-white/[0.14] hover:bg-[#111111] hover:text-zinc-200 disabled:cursor-not-allowed disabled:opacity-35">
                    <RefreshCw size={13.5} className={state.busy ? "animate-spin" : ""} /> Check again
                  </button>
                </div>
              </div>

              <div className="relative mt-5">
                <div className="mb-2 flex min-w-0 items-start justify-between gap-4 text-[9.5px] font-semibold text-zinc-600">
                  <span className="min-w-0 break-words leading-4">{progress.message}</span>
                  <span className="shrink-0">{progress.indeterminate ? "working" : `${percent >= 100 ? 100 : Math.floor(percent)}%`}</span>
                </div>
                <div className="h-1.5 overflow-hidden rounded-full bg-white/[0.045]">
                  {progress.indeterminate ? (
                    <div
                      key="indeterminate-progress"
                      className="indeterminate-progress h-full w-[34%] rounded-full bg-gradient-to-r from-zinc-600 via-zinc-300 to-white"
                    />
                  ) : (
                    <div
                      key="determinate-progress"
                      className={cx(
                        "h-full rounded-full bg-gradient-to-r from-zinc-600 via-zinc-300 to-white",
                        percent < 100 && "transition-[width] duration-200 ease-out"
                      )}
                      style={{ width: `${percent}%` }}
                    />
                  )}
                </div>
              </div>
          </section>

          <div className="secondary-grid mt-4 grid min-w-0 grid-cols-[1.08fr_.92fr] gap-4 max-[980px]:grid-cols-1">
            <section className="glass-card min-w-0 rounded-[18px] p-5">
              <div className="section-heading flex min-w-0 items-start justify-between gap-4">
                <div className="min-w-0">
                  <div className="flex items-center gap-2 text-[12px] font-bold text-white"><Settings2 size={15.5} className="shrink-0 text-zinc-400" /> Quick actions</div>
                  <div className="mt-1.5 text-[10.2px] leading-4 text-zinc-600">Selected client only where applicable.</div>
                </div>
                <TinyStatus tone={state.busy ? "accent" : "neutral"} pulse={state.busy}>{state.busy ? "busy" : "ready"}</TinyStatus>
              </div>
              <div className="action-grid mt-4 grid grid-cols-2 gap-2.5">
                <IconButton icon={Wrench} label="Repair payload" disabled={!actions.repair?.enabled} onClick={() => action("repair")} />
                <IconButton icon={FolderOpen} label="Plugins folder" disabled={!actions.plugins?.enabled} onClick={() => action("plugins")} />
                <IconButton icon={HardDriveDownload} label="Install folder" disabled={!actions.installFolder?.enabled} onClick={() => action("installFolder")} />
                <IconButton icon={Trash2} label="Uninstall client" disabled={!actions.uninstall?.enabled} onClick={() => action("uninstall")} danger />
              </div>
            </section>

            <section id="components-section" className="glass-card min-w-0 scroll-mt-6 rounded-[18px] p-5">
              <div className="section-heading flex min-w-0 items-start justify-between gap-4">
                <div className="min-w-0">
                  <div className="flex items-center gap-2 text-[12px] font-bold text-white"><PackageCheck size={15.5} className="shrink-0 text-zinc-400" /> Included components</div>
                  <div className="mt-1.5 text-[10.2px] leading-4 text-zinc-600">Read directly from the latest manifest.</div>
                </div>
                <TinyStatus tone="success">verified</TinyStatus>
              </div>
              <div className="mt-4 grid gap-2.5">
                <ComponentRow icon={Code2} name="Vencord base" value={state.components?.vencord} />
                <ComponentRow icon={Sparkles} name="OrionQuests" value={state.components?.orion} />
                <ComponentRow icon={Zap} name="NitroSniper" value={state.components?.nitro} />
                <ComponentRow icon={HeartPulse} name="Runtime plugin loader" value={state.components?.loader} accent />
              </div>
            </section>
          </div>

          <section id="activity-section" className="glass-card mt-4 scroll-mt-6 rounded-[18px] p-5">
            <div className="activity-heading flex min-w-0 items-start justify-between gap-5">
              <div className="min-w-0">
                <div className="flex items-center gap-2 text-[12px] font-bold text-white"><Activity size={15.5} className="shrink-0 text-zinc-400" /> Live activity</div>
                <div className="mt-1.5 text-[10.2px] leading-4 text-zinc-600">Native manager operations and verification events.</div>
              </div>
              <div className="activity-actions flex shrink-0 items-center gap-2">
                <button type="button" onClick={() => action("copyLog")} className="grid h-9 w-9 place-items-center rounded-lg border border-white/[0.06] bg-white/[0.02] text-slate-500 transition-colors hover:bg-white/[0.05] hover:text-slate-200" title="Copy activity"><Clipboard size={13.5} /></button>
                <button type="button" onClick={() => action("clearLog")} className="grid h-9 w-9 place-items-center rounded-lg border border-white/[0.06] bg-white/[0.02] text-slate-500 transition-colors hover:bg-white/[0.05] hover:text-slate-200" title="Clear activity"><X size={13.5} /></button>
                <button type="button" onClick={() => setLogExpanded(v => !v)} className="min-h-9 rounded-lg border border-white/[0.06] bg-white/[0.02] px-3 text-[9.8px] font-bold text-slate-500 transition-colors hover:text-slate-200">{logExpanded ? "Collapse" : "Expand"}</button>
              </div>
            </div>
            <div className={cx("activity-log-wrap overflow-hidden transition-[height,opacity,margin] duration-150", logExpanded ? "mt-4 h-44 opacity-100" : "mt-0 h-0 opacity-0")}>
              <div ref={logRef} className="manager-scroll inner-surface h-full overflow-y-auto rounded-2xl p-4 font-mono text-[10.2px] leading-[1.7] text-slate-400 whitespace-pre-wrap break-words">
                {state.log || "Waiting for manager activity…"}
              </div>
            </div>
          </section>
        </section>
      </div>

      {dialog && <ConfirmModal dialog={dialog} onResult={resolveDialog} />}
    </main>
  );
}

createRoot(document.getElementById("root")).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);
