/* Simulated device selection, core layout, and scenario copy. */
import {
  state, previewScenario, setText, t, pick, clamp, randomBetween, randomInt, roundTo, $
} from "./core.js";
import { DEVICE_PROFILES } from "./config.js";

function buildCoreThermals(count, profile) {
  const spread = randomBetween(profile.coreSpread[0], profile.coreSpread[1]);
  const hotCount = Math.max(1, Math.round(count * randomBetween(0.04, 0.12)));
  const hotCores = new Set();
  while (hotCores.size < hotCount) hotCores.add(randomInt(0, count - 1));
  let coldCore = -1;
  if (hotCores.size < count) {
    do {
      coldCore = randomInt(0, count - 1);
    } while (hotCores.has(coldCore));
  }

  return Array.from({ length: count }, (_, index) => {
    const isHot = hotCores.has(index);
    const nextToHot = !isHot && (hotCores.has(index - 1) || hotCores.has(index + 1));
    let offset;
    if (isHot) {
      offset = randomBetween(spread * 0.45, spread * 0.95);
    } else if (index === coldCore) {
      offset = randomBetween(-spread * 0.62, -spread * 0.52);
    } else if (nextToHot) {
      offset = randomBetween(spread * 0.05, spread * 0.28);
    } else {
      offset = randomBetween(-spread * 0.42, spread * 0.18);
    }
    return {
      offset: roundTo(offset, 2),
      phase: randomBetween(0, Math.PI * 2),
      rate: randomBetween(0.35, 0.55)
    };
  });
}

function applyDeviceScenario() {
  const hero = state.hero;
  const profile = pick(DEVICE_PROFILES);
  const cpu = pick(profile.cpus);
  const cores = cpu.cores;
  const maxClock = roundTo(randomBetween(profile.maxClock[0], profile.maxClock[1]), 2);
  const thermalBias = randomBetween(-0.8, 0.8);
  const offTemp = Math.round(randomBetween(profile.offTemp[0], profile.offTemp[1]) + thermalBias);
  const onTemp = Math.round(randomBetween(profile.onTemp[0], profile.onTemp[1]) + thermalBias * 0.6);
  const offPower = Math.round(randomBetween(profile.offPower[0], profile.offPower[1]));
  const onPower = Math.round(randomBetween(profile.onPower[0], profile.onPower[1]));
  const offGpu = Math.round(randomBetween(profile.offGpu[0], profile.offGpu[1]) + thermalBias * 0.5);
  const onGpu = Math.round(randomBetween(profile.onGpu[0], profile.onGpu[1]) + thermalBias * 0.4);
  const offGpuLoad = randomInt(profile.offGpuLoad[0], profile.offGpuLoad[1]);
  const onGpuLoad = randomInt(profile.onGpuLoad[0], profile.onGpuLoad[1]);
  const offGpuPower = Math.round(randomBetween(profile.gpuPower[0], profile.gpuPower[1]));
  const onGpuPower = Math.round(offGpuPower * randomBetween(0.58, 0.78));
  const offGpuFan = offGpuLoad >= 60 ? randomInt(700, 1500) : 0;
  const onGpuFan = onGpuLoad >= 55 ? randomInt(500, 1200) : 0;
  const vramDelta = randomInt(4, 9);
  const ssdTemp = Math.round(randomBetween(profile.ssdOff[0], profile.ssdOff[1]) + thermalBias * 0.4);
  const ssdTempOn = Math.round(randomBetween(profile.ssdOn[0], profile.ssdOn[1]) + thermalBias * 0.3);
  const ssdTempE = Math.round(randomBetween(profile.ssdOff[0], profile.ssdOff[1]) + thermalBias * 0.35);
  const ssdTempEOn = Math.round(randomBetween(profile.ssdOn[0], profile.ssdOn[1]) + thermalBias * 0.3);
  const hotspotDelta = randomInt(profile.hotspot[0], profile.hotspot[1]);
  const ramTotal = pick(profile.ramTotals);
  const ramReserved = roundTo(randomBetween(0.3, 0.8), 1);
  const ramUsed = roundTo(ramTotal * randomBetween(profile.ramUsage[0], profile.ramUsage[1]), 1);
  const available = Math.max(0.8, ramTotal - ramReserved - ramUsed);
  const ramStandby = roundTo(Math.min(available * randomBetween(0.45, 0.72), available - 0.2), 1);
  const ramModified = roundTo(randomBetween(0.1, Math.max(0.2, ramTotal * 0.018)), 1);

  hero.deviceProfile = profile;
  hero.cpuName = cpu.name;
  hero.cores = cores;
  hero.coreThermals = buildCoreThermals(cores, profile);
  hero.coreLift = Math.max(...hero.coreThermals.map((thermal) => thermal.offset), 0);
  hero.maxClock = maxClock;
  hero.temp = offTemp;
  hero.displayTemp = clamp(offTemp + randomBetween(-0.45, 0.45), 25, 100);
  hero.lastDisplayTemp = Math.round(hero.displayTemp);
  hero.cooling = false;
  hero.clock = roundTo(maxClock * randomBetween(profile.offClockRatio[0], profile.offClockRatio[1]), 2);
  hero.power = offPower;
  hero.gpuTemp = offGpu;
  hero.gpuDisplayTemp = clamp(offGpu + randomBetween(-0.35, 0.35), 25, 105);
  hero.gpuDisplayLast = Math.round(hero.gpuDisplayTemp);
  hero.gpuName = pick(profile.gpuNames);
  hero.gpuLoad = offGpuLoad;
  hero.gpuPower = offGpuPower;
  hero.gpuFan = offGpuFan;
  hero.hotspot = offGpu + hotspotDelta;
  hero.vramTemp = offGpu + vramDelta;
  hero.ssdTemp = ssdTemp;
  hero.ssdTempE = ssdTempE;
  hero.ssdDisplayC = ssdTemp + randomBetween(-0.3, 0.3);
  hero.ssdDisplayE = ssdTempE + randomBetween(-0.3, 0.3);
  hero.ssdDisplayLastC = Math.round(hero.ssdDisplayC);
  hero.ssdDisplayLastE = Math.round(hero.ssdDisplayE);
  hero.ssdHealth = randomInt(profile.health[0], profile.health[1]);
  hero.ssdHealthE = randomInt(profile.health[0], profile.health[1]);
  hero.windowMinutes = 1;
  hero.samples.length = 0;
  hero.gpuSamples.length = 0;
  hero.sensorTimer = 0;
  hero.wobblePhase = randomBetween(0, Math.PI * 2);
  hero.chartDirty = true;
  hero.gpuChartDirty = true;
  hero.ramTotal = ramTotal;
  hero.ramInstalled = Math.ceil(ramTotal);
  hero.ramUsed = ramUsed;
  hero.ramTarget = ramUsed;
  hero.ramPercent = (ramUsed / ramTotal) * 100;
  hero.ramTargetPercent = hero.ramPercent;
  hero.ramReserved = ramReserved;
  hero.ramStandby = ramStandby;
  hero.ramStandbyTarget = ramStandby;
  hero.ramModified = ramModified;
  hero.ramModifiedTarget = ramModified;
  hero.ramFreedSize = 0;
  hero.ramBusy = false;
  hero.ramOptimized = false;
  hero.ramStatus = "";
  hero.tooltipTimer = 0;

  // Keep the random targets for the cooling transition.
  hero.deviceTargets = {
    offTemp,
    onTemp,
    offClock: hero.clock,
    onClock: roundTo(maxClock * randomBetween(profile.onClockRatio[0], profile.onClockRatio[1]), 2),
    offPower,
    onPower,
    offGpu,
    onGpu,
    offGpuLoad,
    onGpuLoad,
    offGpuPower,
    onGpuPower,
    offGpuFan,
    onGpuFan,
    offHotspot: offGpu + hotspotDelta,
    onHotspot: onGpu + Math.max(6, hotspotDelta - 1),
    offVram: offGpu + vramDelta,
    onVram: onGpu + Math.max(3, vramDelta - 2),
    offSsd: ssdTemp,
    onSsd: ssdTempOn,
    offSsdE: ssdTempE,
    onSsdE: ssdTempEOn
  };

  renderScenario();
}

function renderScenario() {
  const hero = state.hero;
  if (previewScenario && hero.deviceProfile) {
    setText(previewScenario, t(hero.deviceProfile.nameKey) + " · " + t("scenario.simulated"));
  }
  setText($("#hero-cpu-name"), hero.cpuName);
  setText($("#hero-cores"), t("hero.processorLabel", { count: hero.cores }));
}

function gpuBrand(name) {
  if (/geforce|nvidia|\brtx\b|\bgtx\b/i.test(name)) return "NVIDIA";
  if (/radeon|\brx\b|\bamd\b/i.test(name)) return "AMD";
  if (/intel|iris|\barc\b/i.test(name)) return "Intel";
  return "GPU";
}


export { applyDeviceScenario, renderScenario, gpuBrand };
