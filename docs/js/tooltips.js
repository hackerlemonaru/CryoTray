/* Popovers, CPU/RAM/GPU/SSD detail cards, and tooltip wiring. */
import {
  state, appPreview, cpuPopGrid, t, setText, $, $$, clamp, alignCoreTemps, formatGb,
  reduceMotion
} from "./core.js";

function getMemoryMetrics() {
  const hero = state.hero;
  const total = hero.ramTotal;
  const inUse = clamp(hero.ramUsed, 0, Math.max(0.2, total - hero.ramReserved - 0.2));
  const available = Math.max(0, total - hero.ramReserved - inUse);
  const standby = clamp(hero.ramStandby, 0, Math.max(0, available - 0.1));
  const modified = clamp(hero.ramModified, 0, Math.max(0, available - standby));
  const free = Math.max(0, available - standby);
  const denom = Math.max(0.1, inUse + modified + standby + free);
  return {
    total,
    inUse,
    available,
    standby,
    modified,
    free,
    cached: standby + modified,
    denom,
    inUsePct: (inUse / total) * 100
  };
}

function cpuColumns() {
  if (window.innerWidth < 480) return 4;
  return state.hero.cores > 16 ? 6 : 4;
}

function renderTooltips() {
  const hero = state.hero;
  if (!hero.deviceTargets) return;

  setText($("#cpu-pop-footer"), t("hero.packagePower", { power: Math.round(hero.power) }));
  const grid = cpuPopGrid;
  if (grid) {
    if (grid.dataset.cores !== String(hero.cores)) {
      grid.innerHTML = "";
      for (let index = 0; index < hero.cores; index += 1) {
        const cell = document.createElement("span");
        cell.className = "cpu-core-cell";
        grid.appendChild(cell);
      }
      grid.dataset.cores = String(hero.cores);
    }
    const columns = cpuColumns();
    grid.style.gridTemplateColumns = "repeat(" + columns + ", minmax(0, 1fr))";
    grid.dataset.columns = String(columns);
    if (grid.parentElement) grid.parentElement.classList.toggle("has-six-columns", columns === 6);
    const temps = [];
    const cells = $$(".cpu-core-cell", grid);
    const seconds = performance.now() / 1000;
    cells.forEach((cell, index) => {
      const thermal = hero.coreThermals[index] || { offset: 0, phase: 0, rate: 0.45 };
      const wave = reduceMotion.matches ? 0 : Math.sin(seconds * thermal.rate + thermal.phase) * 0.55;
      temps.push(Math.round(hero.displayTemp + thermal.offset + wave));
    });
    const aligned = alignCoreTemps(temps, Math.round(clamp(hero.displayTemp + hero.coreLift, 25, 100)));
    const hottest = Math.max(...aligned);
    cells.forEach((cell, index) => {
      cell.textContent = "CPU" + String(index).padStart(2, "0") + " " + aligned[index] + "°C";
      cell.classList.toggle("is-hot", aligned[index] === hottest);
    });
  }

  const memory = getMemoryMetrics();
  setText($("#ram-installed"), t("memory.installed", { size: hero.ramInstalled.toFixed(1) }));
  setText($("#ram-inuse"), formatGb(memory.inUse) + " (" + Math.round(memory.inUsePct) + "%)");
  setText($("#ram-available"), formatGb(memory.available) + " (" + Math.max(0, 100 - Math.round(memory.inUsePct)) + "%)");
  setText($("#ram-free"), formatGb(memory.free));
  setText($("#ram-standby"), formatGb(memory.standby));
  setText($("#ram-modified"), formatGb(memory.modified));
  setText($("#ram-cached"), formatGb(memory.cached));
  setText($("#ram-reserved"), formatGb(hero.ramReserved));
  const widthFor = (value) => (value / memory.denom) * 100;
  const bar = (id, value) => {
    const element = $(id);
    if (element) element.style.width = widthFor(value).toFixed(2) + "%";
  };
  bar("#ram-bar-inuse", memory.inUse);
  bar("#ram-bar-modified", memory.modified);
  bar("#ram-bar-standby", memory.standby);
  bar("#ram-bar-free", memory.free);
  const footer = $("#ram-pop-foot");
  if (footer) footer.textContent = hero.ramBusy ? t("hero.ramBusy") : t("memory.footer");

  setText($("#gpu-pop-name"), hero.gpuName || t("hero.demoGpu"));
  setText($("#gpu-pop-load"), Math.round(hero.gpuLoad) + "%");
  setText($("#gpu-pop-hotspot"), Math.round(hero.hotspot) + "°C");
  setText($("#gpu-pop-delta"), Math.max(0, Math.round(hero.hotspot - hero.gpuDisplayTemp)) + "°C");
  setText($("#gpu-pop-power"), Math.round(hero.gpuPower) + " W");
  setText($("#gpu-pop-vram"), Math.round(hero.vramTemp) + "°C");
  setText($("#gpu-pop-fan"), Math.round(hero.gpuFan) + " RPM");
  setText($("#ssd-pop-c"), Math.round(hero.ssdHealth) + "% · " + Math.round(hero.ssdDisplayC) + "°C");
  setText($("#ssd-pop-e"), Math.round(hero.ssdHealthE) + "% · " + Math.round(hero.ssdDisplayE) + "°C");
  setText($("#ssd-pop-health"), t("hero.smartHealthy"));
}

let activePopover = null;
let activeTrigger = null;
let popoverPinned = false;
let popoverCloseTimer = 0;
let suppressFocusOpen = false;

function positionPopover(trigger, popover) {
  if (!trigger || !popover) return;
  const host = appPreview.getBoundingClientRect();
  const rect = trigger.getBoundingClientRect();
  const popWidth = popover.offsetWidth;
  const popHeight = popover.offsetHeight;
  const gap = 9;
  const pad = 8;
  const spaceAbove = rect.top - host.top - pad;
  const spaceBelow = host.bottom - rect.bottom - pad;
  let placement = "above";
  if (spaceAbove < popHeight + gap && spaceBelow >= spaceAbove) placement = "below";

  let top = placement === "above"
    ? rect.top - host.top - popHeight - gap
    : rect.bottom - host.top + gap;
  const maxTop = Math.max(pad, host.height - popHeight - pad);
  top = clamp(top, pad, maxTop);
  const triggerCenter = rect.left - host.left + rect.width / 2;
  const maxLeft = Math.max(pad, host.width - popWidth - pad);
  const left = clamp(triggerCenter - popWidth / 2, pad, maxLeft);

  popover.dataset.placement = placement;
  popover.style.top = Math.round(top) + "px";
  popover.style.left = Math.round(left) + "px";
  popover.style.setProperty("--caret-x", Math.round(clamp(triggerCenter - left, 14, popWidth - 14)) + "px");
}

function openPopover(trigger, popover, pinned) {
  window.clearTimeout(popoverCloseTimer);
  if (activePopover && activePopover !== popover) closePopover(false);
  activePopover = popover;
  activeTrigger = trigger;
  popoverPinned = Boolean(pinned);
  renderTooltips();
  popover.classList.add("is-open");
  popover.setAttribute("aria-hidden", "false");
  trigger.setAttribute("aria-expanded", "true");
  trigger.setAttribute("aria-describedby", popover.id);
  positionPopover(trigger, popover);
}

function closePopover(restoreFocus) {
  window.clearTimeout(popoverCloseTimer);
  if (!activePopover) return;
  const popover = activePopover;
  const trigger = activeTrigger;
  popover.classList.remove("is-open");
  popover.setAttribute("aria-hidden", "true");
  if (trigger) {
    trigger.setAttribute("aria-expanded", "false");
    trigger.removeAttribute("aria-describedby");
  }
  activePopover = null;
  activeTrigger = null;
  popoverPinned = false;
  if (restoreFocus && trigger) {
    suppressFocusOpen = true;
    trigger.focus();
    suppressFocusOpen = false;
  }
}

function bindTooltips() {
  $$("[data-tooltip-target]").forEach((trigger) => {
    const popover = $("#" + trigger.dataset.tooltipTarget);
    if (!popover) return;
    trigger.addEventListener("pointerenter", () => {
      window.clearTimeout(popoverCloseTimer);
      if (activePopover !== popover) openPopover(trigger, popover, false);
    });
    trigger.addEventListener("pointerleave", () => {
      if (activePopover === popover && !popoverPinned) {
        window.clearTimeout(popoverCloseTimer);
        popoverCloseTimer = window.setTimeout(() => closePopover(false), 120);
      }
    });
    trigger.addEventListener("focus", () => {
      if (!suppressFocusOpen && activePopover !== popover) openPopover(trigger, popover, false);
    });
    trigger.addEventListener("blur", () => {
      if (activePopover === popover) closePopover(false);
    });
    trigger.addEventListener("click", () => {
      window.clearTimeout(popoverCloseTimer);
      if (activePopover !== popover) {
        openPopover(trigger, popover, true);
      } else if (!popoverPinned) {
        popoverPinned = true;
      } else {
        closePopover(true);
      }
    });
  });

  document.addEventListener("pointerdown", (event) => {
    if (!activePopover) return;
    if (activeTrigger && activeTrigger.contains(event.target)) return;
    if (activePopover.contains(event.target)) return;
    closePopover(false);
  });

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && activePopover) {
      closePopover(true);
    }
  });

  window.addEventListener("resize", () => {
    if (activePopover && activeTrigger) positionPopover(activeTrigger, activePopover);
  });
}


export function repositionActivePopover() {
  if (activePopover && activeTrigger) positionPopover(activeTrigger, activePopover);
}

export { renderTooltips, bindTooltips, closePopover };
