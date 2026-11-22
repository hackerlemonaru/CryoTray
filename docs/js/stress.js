/* Simulated stress test state, controls, metrics, chart, and preview timing. */
import {
  state, t, $, setText, stressStart, stressToggle, stressState,
  stressCoolingLabel, stressProfileLabel, stressPeak, stressPower, stressAvgTemp,
  stressSamples, stressPeakTime, stressLoad, stressLoadFill, stressChartTip,
  clamp, randomBetween, randomInt, pick, roundTo, formatElapsed, signed, signedPercent
} from "./core.js";
import { STRESS_PROFILES, STRESS_PREVIEW_TOGGLE_AT, STRESS_PREVIEW_DURATION } from "./config.js";
import { chartState, hideChartTip } from "./chart.js";
import { drawStressChart } from "./stress-chart.js";
function setStressState(kind) {
  stressState.dataset.state = kind;
  const key = kind === "cooling"
    ? "stress.stateCooling"
    : kind === "running"
      ? "stress.stateRunning"
      : kind === "stopped"
        ? "stress.stateStopped"
        : kind === "preview"
          ? "stress.statePreviewDone"
          : "stress.stateReady";
  setText($("#stress-state-text"), t(key));
}

function setStressCooling(cooling) {
  state.stress.cooling = cooling;
  stressToggle.setAttribute("aria-pressed", String(cooling));
  stressCoolingLabel.classList.toggle("is-on", cooling);
  setText(stressCoolingLabel, t(cooling ? "stress.on" : "stress.off"));
  setStressState(state.stress.running ? (cooling ? "cooling" : "running") : "ready");
}

function applyStressProfile(previousId, initial) {
  const pool = STRESS_PROFILES.filter((item) => item.id !== previousId);
  const profile = pick(pool.length ? pool : STRESS_PROFILES);
  const offTemp = Math.round(randomBetween(profile.offTemp[0], profile.offTemp[1]));
  const onTemp = Math.min(
    Math.round(randomBetween(profile.onTemp[0], profile.onTemp[1])),
    offTemp - randomInt(9, 17)
  );
  const offPower = Math.round(randomBetween(profile.offPower[0], profile.offPower[1]));
  const onPower = Math.min(
    Math.round(randomBetween(profile.onPower[0], profile.onPower[1])),
    Math.round(offPower * randomBetween(0.65, 0.84))
  );
  const offSpeed = roundTo(randomBetween(profile.offSpeed[0], profile.offSpeed[1]), 2);
  const onSpeed = Math.min(
    roundTo(randomBetween(profile.onSpeed[0], profile.onSpeed[1]), 2),
    roundTo(offSpeed - randomBetween(0.35, 0.8), 2)
  );
  const targets = {
    id: profile.id,
    nameKey: profile.nameKey,
    threads: pick(profile.threads),
    offTemp,
    onTemp,
    offPower,
    onPower,
    offSpeed,
    onSpeed
  };
  const idle = {
    temp: randomInt(profile.idleTemp[0], profile.idleTemp[1]),
    speed: roundTo(randomBetween(2.7, 3.3), 2),
    power: randomInt(14, 22)
  };

  state.stress.profile = profile;
  state.stress.targets = targets;
  state.stress.idle = idle;
  if (initial) {
    state.stress.temp = idle.temp;
    state.stress.displayTemp = idle.temp;
    state.stress.lastDisplayTemp = Math.round(idle.temp);
    state.stress.speed = idle.speed;
    state.stress.power = idle.power;
    state.stress.peak = idle.temp;
    state.stress.peakTime = 0;
    state.stress.elapsed = 0;
    state.stress.load = 0;
    state.stress.sensorTimer = 0;
    state.stress.phase = randomBetween(0, Math.PI * 2);
  }
  setText(
    stressProfileLabel,
    t("stress.profileLabel", { profile: t(targets.nameKey), threads: targets.threads })
  );
}

function startStress(autoMode = false) {
  const stress = state.stress;
  applyStressProfile(stress.targets ? stress.targets.id : null, false);
  stress.running = true;
  stress.started = true;
  stress.autoMode = Boolean(autoMode);
  stress.autoCooled = false;
  stress.previewComplete = false;
  stress.cooling = false;
  stress.elapsed = 0;
  stress.temp = stress.idle.temp;
  stress.displayTemp = stress.idle.temp;
  stress.lastDisplayTemp = Math.round(stress.idle.temp);
  stress.speed = stress.idle.speed;
  stress.power = stress.idle.power;
  stress.peak = stress.idle.temp;
  stress.peakTime = 0;
  stress.load = 0;
  stress.sensorTimer = 0;
  stress.phase = randomBetween(0, Math.PI * 2);
  stress.loadMax = randomBetween(0.94, 1);
  stress.ramp = randomBetween(1.8, 3.4);
  stress.samples = [];
  stress.chartSummaryTimer = 0;
  chartState.stressHoverSample = null;
  hideChartTip(stressChartTip);

  stressStart.textContent = t("stress.stop");
  stressStart.classList.remove("btn-primary");
  stressStart.classList.add("btn-danger");
  stressToggle.disabled = false;
  stressToggle.setAttribute("aria-pressed", "false");
  stressCoolingLabel.classList.remove("is-on");
  setText(stressCoolingLabel, t("stress.off"));
  setStressState("running");
  renderStressUI();
  drawStressChart();
}

function stopStress(previewComplete = false) {
  const stress = state.stress;
  stress.running = false;
  stress.autoMode = false;
  stress.previewComplete = previewComplete;
  stress.cooling = false;
  stressStart.textContent = t("stress.start");
  stressStart.classList.remove("btn-danger");
  stressStart.classList.add("btn-primary");
  stressToggle.disabled = true;
  stressToggle.setAttribute("aria-pressed", "false");
  stressCoolingLabel.classList.remove("is-on");
  setText(stressCoolingLabel, t("stress.off"));
  setStressState(previewComplete ? "preview" : "stopped");
  chartState.stressHoverSample = null;
  hideChartTip(stressChartTip);
  renderStressUI();
  drawStressChart();
}

function toggleStressCooling() {
  if (!state.stress.running) return;
  setStressCooling(!state.stress.cooling);
  setStressState(state.stress.cooling ? "cooling" : "running");
  drawStressChart();
}

function renderStressUI() {
  const stress = state.stress;
  const targets = stress.targets;
  if (!targets) return;
  const offSamples = stress.samples.filter((sample) => !sample.cooling);
  const onSamples = stress.samples.filter((sample) => sample.cooling);
  const hasOff = offSamples.length > 0;
  const hasOn = onSamples.length > 0;
  const hasBoth = hasOff && hasOn;
  const average = (samples, key, fallback) => samples.length
    ? samples.reduce((sum, sample) => sum + sample[key], 0) / samples.length
    : fallback;
  const peakOf = (samples, fallback) => samples.length
    ? Math.max(...samples.map((sample) => sample.temp))
    : fallback;
  const settled = (samples) => samples.slice(-Math.min(4, samples.length));
  const settledAverage = (samples, key, fallback) => average(settled(samples), key, fallback);
  const offSteadyTemp = settledAverage(offSamples, "temp", targets.offTemp);
  const onSteadyTemp = settledAverage(onSamples, "temp", targets.onTemp);
  const offSteadyPower = settledAverage(offSamples, "power", targets.offPower);
  const onSteadyPower = settledAverage(onSamples, "power", targets.onPower);
  const offSteadySpeed = settledAverage(offSamples, "speed", targets.offSpeed);
  const onSteadySpeed = settledAverage(onSamples, "speed", targets.onSpeed);
  const currentAvg = stress.samples.length
    ? stress.samples.reduce((sum, sample) => sum + sample.temp, 0) / stress.samples.length
    : null;
  const deltaTemp = onSteadyTemp - offSteadyTemp;
  const deltaPower = onSteadyPower - offSteadyPower;
  const deltaSpeed = onSteadySpeed - offSteadySpeed;

  setText($("#stress-temp"), stress.started ? Math.round(stress.displayTemp) + " °C" : "-- °C");
  setText($("#stress-speed"), stress.started ? stress.speed.toFixed(2) + " GHz" : "-- GHz");
  setText($("#stress-elapsed"), formatElapsed(stress.elapsed));
  setText(stressPeak, stress.started ? Math.round(Math.max(stress.peak, stress.displayTemp)) + " °C" : "-- °C");
  setText(stressPower, stress.started ? Math.round(stress.power) + " W" : "-- W");
  setText(stressAvgTemp, currentAvg == null ? "-- °C" : Math.round(currentAvg) + " °C");
  setText(stressSamples, String(stress.samples.length));
  setText(stressPeakTime, stress.samples.length ? formatElapsed(stress.peakTime) : "--:--");
  setText(stressLoad, Math.round(stress.load * 100) + "%");
  if (stressLoadFill) stressLoadFill.style.width = Math.round(stress.load * 100) + "%";
  setText(
    stressProfileLabel,
    t("stress.profileLabel", { profile: t(targets.nameKey), threads: targets.threads })
  );

  if (hasOff) {
    setText($("#off-temp"), String(Math.round(offSteadyTemp)));
    setText($("#off-power"), String(Math.round(offSteadyPower)));
    setText($("#off-detail"), t("stress.cardDetail", {
      peak: Math.round(peakOf(settled(offSamples), targets.offTemp)),
      avg: Math.round(offSteadyTemp),
      speed: offSteadySpeed.toFixed(2)
    }));
  } else {
    setText($("#off-temp"), "--");
    setText($("#off-power"), "--");
    setText($("#off-detail"), t("stress.cardPending"));
  }

  if (hasOn) {
    setText($("#on-temp"), String(Math.round(onSteadyTemp)));
    setText($("#on-power"), String(Math.round(onSteadyPower)));
    setText($("#on-detail"), t("stress.cardDetail", {
      peak: Math.round(peakOf(settled(onSamples), targets.onTemp)),
      avg: Math.round(onSteadyTemp),
      speed: onSteadySpeed.toFixed(2)
    }));
  } else {
    setText($("#on-temp"), "--");
    setText($("#on-power"), "--");
    setText($("#on-detail"), t("stress.cardPending"));
  }

  if (hasBoth) {
    setText($("#delta-temp"), signed(deltaTemp));
    setText($("#delta-power"), signed(deltaPower));
    setText($("#delta-temp-detail"), signed(deltaTemp));
    setText($("#delta-temp-pct"), signedPercent(deltaTemp, offSteadyTemp));
    setText($("#delta-power-detail"), signed(deltaPower));
    setText($("#delta-power-pct"), signedPercent(deltaPower, offSteadyPower));
    setText($("#delta-clock"), signed(deltaSpeed, 2));
    setText($("#delta-clock-pct"), signedPercent(deltaSpeed, offSteadySpeed));
    setText($("#delta-summary"), t("stress.deltaSummary", {
      temp: Math.abs(Math.round(deltaTemp)),
      power: Math.abs(Math.round(deltaPower))
    }));
  } else {
    setText($("#delta-temp"), "--");
    setText($("#delta-power"), "--");
    setText($("#delta-temp-detail"), "--");
    setText($("#delta-temp-pct"), "");
    setText($("#delta-power-detail"), "--");
    setText($("#delta-power-pct"), "");
    setText($("#delta-clock"), "--");
    setText($("#delta-clock-pct"), "");
    setText($("#delta-summary"), t("stress.deltaPending"));
  }

  setText(stressStart, t(stress.running ? "stress.stop" : "stress.start"));
  stressStart.classList.toggle("btn-danger", stress.running);
  stressStart.classList.toggle("btn-primary", !stress.running);
  stressToggle.disabled = !stress.running;
  stressToggle.setAttribute("aria-pressed", String(stress.cooling));
  setText(stressCoolingLabel, t(stress.cooling ? "stress.on" : "stress.off"));
  stressCoolingLabel.classList.toggle("is-on", stress.cooling);
  setStressState(
    stress.running
      ? (stress.cooling ? "cooling" : "running")
      : stress.previewComplete
        ? "preview"
        : stress.started
          ? "stopped"
          : "ready"
  );
}

function updateStress(dt) {
  const stress = state.stress;
  const targets = stress.targets;
  if (!targets) return;

  if (stress.running) {
    stress.elapsed += dt;
    if (stress.autoMode) {
      if (!stress.autoCooled && stress.elapsed >= STRESS_PREVIEW_TOGGLE_AT) {
        stress.autoCooled = true;
        setStressCooling(true);
      }
      if (stress.elapsed >= STRESS_PREVIEW_DURATION) {
        stopStress(true);
        return;
      }
    }
    stress.load = Math.min(stress.loadMax, stress.load + (dt / stress.ramp) * stress.loadMax);
    const loadFactor = stress.load;
    const fullTemp = stress.cooling ? targets.onTemp : targets.offTemp;
    const fullSpeed = stress.cooling ? targets.onSpeed : targets.offSpeed;
    const fullPower = stress.cooling ? targets.onPower : targets.offPower;
    const targetTemp = stress.idle.temp + (fullTemp - stress.idle.temp) * loadFactor;
    const targetSpeed = stress.idle.speed + (fullSpeed - stress.idle.speed) * loadFactor;
    const targetPower = stress.idle.power + (fullPower - stress.idle.power) * loadFactor;
    stress.temp += (targetTemp - stress.temp) * (1 - Math.exp(-dt * (stress.cooling ? 0.86 : 0.48)));
    stress.speed += (targetSpeed - stress.speed) * (1 - Math.exp(-dt * 1.4));
    stress.power += (targetPower - stress.power) * (1 - Math.exp(-dt * 1.4));
  } else {
    stress.load += (0 - stress.load) * (1 - Math.exp(-dt * 3.2));
    stress.temp += (stress.idle.temp - stress.temp) * (1 - Math.exp(-dt * 0.45));
    stress.speed += (stress.idle.speed - stress.speed) * (1 - Math.exp(-dt * 0.8));
    stress.power += (stress.idle.power - stress.power) * (1 - Math.exp(-dt * 0.8));
  }

  stress.sensorTimer += dt;
  if (stress.sensorTimer >= 0.5) {
    stress.sensorTimer = 0;
    stress.phase += randomBetween(0.65, 1.25);
    let nextTemp = stress.temp
      + Math.sin(stress.phase) * 0.85
      + Math.sin(stress.phase * 2.1 + 0.7) * 0.28;
    const roundedTemp = Math.round(nextTemp);
    if (roundedTemp === stress.lastDisplayTemp) {
      nextTemp = roundedTemp + (nextTemp >= roundedTemp ? 0.56 : -0.56);
    }
    stress.displayTemp = clamp(nextTemp, 25, 105);
    stress.lastDisplayTemp = Math.round(stress.displayTemp);

    if (stress.running) {
      if (stress.displayTemp > stress.peak) {
        stress.peak = stress.displayTemp;
        stress.peakTime = stress.elapsed;
      }
      stress.samples.push({
        t: stress.elapsed,
        temp: stress.displayTemp,
        cooling: stress.cooling,
        power: stress.power + randomBetween(-0.8, 0.8),
        speed: stress.speed
      });
      if (stress.samples.length > 240) stress.samples.shift();
    }
  }

  renderStressUI();
  drawStressChart();

  stress.chartSummaryTimer += dt;
  if (stress.chartSummaryTimer >= 1) {
    stress.chartSummaryTimer = 0;
    setText(
      $("#stress-chart-summary"),
      t("stress.chartSummary", {
        off: Math.round(targets.offTemp),
        on: Math.round(targets.onTemp),
        temp: Math.round(stress.displayTemp)
      })
    );
  }
}


export {
  applyStressProfile, startStress, stopStress, toggleStressCooling,
  renderStressUI, updateStress, setStressState
};
