/* Hero metrics, RAM cleanup, fan animation, and the live update loop. */
import {
  state, t, $, setText, appPreview, heroSwitch, heroSwitchState, heroFanRotor, ramButton, ramStatus,
  clamp, randomBetween, roundTo, formatGb, reduceMotion
} from "./core.js";
import { gpuBrand } from "./device.js";
import { renderTooltips } from "./tooltips.js";
import { drawHeroChart, drawGpuSpark } from "./hero-chart.js";

function setHeroCooling(cooling) {
  state.hero.cooling = cooling;
  appPreview.classList.toggle("is-cooling", cooling);
  heroSwitch.setAttribute("aria-checked", String(cooling));
  setText(heroSwitchState, t(cooling ? "hero.on" : "hero.off"));
}

function renderHero() {
  const hero = state.hero;
  setText($("#hero-temp"), String(Math.round(clamp(hero.displayTemp + hero.coreLift, 25, 100))));
  setText($("#hero-clock"), hero.clock.toFixed(2) + " / " + hero.maxClock.toFixed(2));
  setText($("#hero-power"), Math.round(hero.power) + "W");
  setText($("#hero-gpu-brand"), gpuBrand(hero.gpuName));
  setText($("#hero-gpu-temp"), String(Math.round(hero.gpuDisplayTemp)));
  setText($("#hero-gpu-power"), String(Math.round(hero.gpuPower)));
  setText($("#hero-hotspot"), String(Math.round(hero.hotspot)));
  setText($("#hero-vram"), String(Math.round(hero.vramTemp)));
  setText($("#hero-gpu-fan"), String(Math.round(hero.gpuFan)));
  setText($("#hero-ssd-temp"), String(Math.round(hero.ssdDisplayC)));
  setText($("#hero-ssd-c-health"), String(Math.round(hero.ssdHealth)));
  setText($("#hero-ssd-e-temp"), String(Math.round(hero.ssdDisplayE)));
  setText($("#hero-ssd-e-health"), String(Math.round(hero.ssdHealthE)));
  setText(heroSwitchState, t(hero.cooling ? "hero.on" : "hero.off"));
}

function renderRam() {
  setText($("#ram-ratio"), state.hero.ramUsed.toFixed(1) + " / " + state.hero.ramTotal.toFixed(1));
  setText($("#ram-percent"), Math.round(state.hero.ramPercent) + "%");

  if (state.hero.ramBusy) {
    setText(ramStatus, t("hero.ramBusy"));
    ramStatus.classList.remove("is-error");
  } else if (state.hero.ramOptimized) {
    setText(ramStatus, t("hero.ramFreed", {
      size: formatGb(state.hero.ramFreedSize),
      percent: Math.round(state.hero.ramTargetPercent)
    }));
    ramStatus.classList.remove("is-error");
  } else {
    setText(ramStatus, state.hero.ramStatus);
  }

  ramButton.disabled = state.hero.ramBusy || state.hero.ramOptimized;
  ramButton.classList.toggle("is-busy", state.hero.ramBusy);
  ramButton.setAttribute("aria-label", t(state.hero.ramOptimized ? "hero.ramOptimized" : "hero.ramOptimize"));
}

function optimizeRam() {
  if (state.hero.ramBusy || state.hero.ramOptimized) return;
  state.hero.ramBusy = true;
  state.hero.ramStatus = "";
  renderRam();

  window.setTimeout(() => {
    const hero = state.hero;
    const requested = roundTo(randomBetween(1.1, 1.5 + hero.ramTotal * 0.055), 1);
    const target = Math.max(hero.ramTotal * 0.28, hero.ramUsed - requested);
    hero.ramBusy = false;
    hero.ramOptimized = true;
    hero.ramFreedSize = roundTo(hero.ramUsed - target, 1);
    hero.ramTarget = roundTo(target, 1);
    hero.ramTargetPercent = (hero.ramTarget / hero.ramTotal) * 100;
    hero.ramStandbyTarget = roundTo(Math.max(0.5, hero.ramStandby * 0.48), 1);
    hero.ramModifiedTarget = roundTo(Math.max(0.1, hero.ramModified * 0.4), 1);
    renderRam();
    renderTooltips();
  }, 820);
}


function updateHero(dt) {
  const hero = state.hero;
  const cooling = hero.cooling;
  const targets = hero.deviceTargets;
  if (!targets) return;
  const tempTarget = cooling ? targets.onTemp : targets.offTemp;
  const clockTarget = cooling ? targets.onClock : targets.offClock;
  const powerTarget = cooling ? targets.onPower : targets.offPower;
  const gpuTarget = cooling ? targets.onGpu : targets.offGpu;
  const gpuLoadTarget = cooling ? targets.onGpuLoad : targets.offGpuLoad;
  const gpuPowerTarget = cooling ? targets.onGpuPower : targets.offGpuPower;
  const gpuFanTarget = cooling ? targets.onGpuFan : targets.offGpuFan;
  const hotspotTarget = cooling ? targets.onHotspot : targets.offHotspot;
  const vramTarget = cooling ? targets.onVram : targets.offVram;
  const ssdTarget = cooling ? targets.onSsd : targets.offSsd;
  const ssdETarget = cooling ? targets.onSsdE : targets.offSsdE;

  hero.temp += (tempTarget - hero.temp) * (1 - Math.exp(-dt * (cooling ? 0.72 : 0.5)));
  hero.clock += (clockTarget - hero.clock) * (1 - Math.exp(-dt * 2.2));
  hero.power += (powerTarget - hero.power) * (1 - Math.exp(-dt * 2.2));
  hero.gpuTemp += (gpuTarget - hero.gpuTemp) * (1 - Math.exp(-dt * 0.8));
  hero.gpuLoad += (gpuLoadTarget - hero.gpuLoad) * (1 - Math.exp(-dt * 1.5));
  hero.gpuPower += (gpuPowerTarget - hero.gpuPower) * (1 - Math.exp(-dt * 1.2));
  hero.gpuFan += (gpuFanTarget - hero.gpuFan) * (1 - Math.exp(-dt * 0.9));
  hero.hotspot += (hotspotTarget - hero.hotspot) * (1 - Math.exp(-dt * 0.7));
  hero.vramTemp += (vramTarget - hero.vramTemp) * (1 - Math.exp(-dt * 0.7));
  hero.ssdTemp += (ssdTarget - hero.ssdTemp) * (1 - Math.exp(-dt * 0.6));
  hero.ssdTempE += (ssdETarget - hero.ssdTempE) * (1 - Math.exp(-dt * 0.55));

  hero.ramUsed += (hero.ramTarget - hero.ramUsed) * (1 - Math.exp(-dt * 4.4));
  hero.ramPercent += (hero.ramTargetPercent - hero.ramPercent) * (1 - Math.exp(-dt * 4.4));
  hero.ramStandby += (hero.ramStandbyTarget - hero.ramStandby) * (1 - Math.exp(-dt * 2.6));
  hero.ramModified += (hero.ramModifiedTarget - hero.ramModified) * (1 - Math.exp(-dt * 2.6));

  hero.sensorTimer += dt;
  if (hero.sensorTimer >= 1) {
    hero.sensorTimer = 0;
    hero.wobblePhase += randomBetween(0.8, 1.35);

    let nextCpu = hero.temp
      + Math.sin(hero.wobblePhase) * 0.8
      + Math.sin(hero.wobblePhase * 2.2 + 0.9) * 0.28;
    const roundedCpu = Math.round(nextCpu);
    if (roundedCpu === hero.lastDisplayTemp) {
      nextCpu = roundedCpu + (nextCpu >= roundedCpu ? 0.56 : -0.56);
    }
    hero.displayTemp = clamp(nextCpu, 25, 100);
    hero.lastDisplayTemp = Math.round(hero.displayTemp);

    let nextGpu = hero.gpuTemp
      + Math.sin(hero.wobblePhase * 1.3 + 1.4) * 0.55
      + Math.sin(hero.wobblePhase * 0.6) * 0.2;
    const roundedGpu = Math.round(nextGpu);
    if (roundedGpu === hero.gpuDisplayLast) {
      nextGpu = roundedGpu + (nextGpu >= roundedGpu ? 0.56 : -0.56);
    }
    hero.gpuDisplayTemp = clamp(nextGpu, 25, 105);
    hero.gpuDisplayLast = Math.round(hero.gpuDisplayTemp);

    let nextC = hero.ssdTemp + Math.sin(hero.wobblePhase * 0.8 + 0.4) * 0.4;
    const roundedC = Math.round(nextC);
    if (roundedC === hero.ssdDisplayLastC) {
      nextC = roundedC + (nextC >= roundedC ? 0.56 : -0.56);
    }
    hero.ssdDisplayC = clamp(nextC, 20, 90);
    hero.ssdDisplayLastC = Math.round(hero.ssdDisplayC);

    let nextE = hero.ssdTempE + Math.sin(hero.wobblePhase * 0.74 + 2.1) * 0.4;
    const roundedE = Math.round(nextE);
    if (roundedE === hero.ssdDisplayLastE) {
      nextE = roundedE + (nextE >= roundedE ? 0.56 : -0.56);
    }
    hero.ssdDisplayE = clamp(nextE, 20, 90);
    hero.ssdDisplayLastE = Math.round(hero.ssdDisplayE);

    hero.samples.push(hero.displayTemp + hero.coreLift);
    hero.sampleModes.push(cooling);
    if (hero.samples.length > 3600) hero.samples.shift();
    if (hero.sampleModes.length > 3600) hero.sampleModes.shift();
    hero.gpuSamples.push(clamp(hero.gpuLoad + randomBetween(-1.5, 1.5), 0, 100));
    if (hero.gpuSamples.length > 48) hero.gpuSamples.shift();
    hero.chartDirty = true;
    hero.gpuChartDirty = true;
  }

  if (!reduceMotion.matches) {
    const fanTarget = cooling ? 250 : 15;
    hero.fanSpeed += (fanTarget - hero.fanSpeed) * (1 - Math.exp(-dt * (cooling ? 2.6 : 0.7)));
    hero.fanAngle = (hero.fanAngle + hero.fanSpeed * dt) % 360;
    heroFanRotor.style.transform = "rotate(" + hero.fanAngle.toFixed(2) + "deg)";
  }

  renderHero();
  renderRam();
  if (hero.chartDirty) drawHeroChart();
  if (hero.gpuChartDirty) drawGpuSpark();

  hero.tooltipTimer += dt;
  if (hero.tooltipTimer >= 0.3) {
    hero.tooltipTimer = 0;
    renderTooltips();
  }
}


export { setHeroCooling, renderHero, renderRam, optimizeRam, updateHero };
