/* Application bootstrap, chart hover, event wiring, and animation loop. */
import {
  state, $, reduceMotion,
  heroSwitch, heroWindowSelect, ramButton, stressStart, stressToggle, copyButton,
  heroCanvas, stressCanvas, gpuSparkCanvas, heroChartTip, stressChartTip, formatElapsed
} from "./core.js";
import { SEED_RELEASE } from "./config.js";
import { applyDeviceScenario, renderScenario } from "./device.js";
import {
  setHeroCooling, renderRam, optimizeRam, updateHero
} from "./hero.js";
import { seedHeroSamples, drawHeroChart, drawGpuSpark } from "./hero-chart.js";
import {
  bindTooltips, renderTooltips, closePopover, repositionActivePopover
} from "./tooltips.js";
import {
  applyStressProfile, startStress, stopStress, toggleStressCooling,
  renderStressUI, updateStress, setStressState
} from "./stress.js";
import { drawStressChart } from "./stress-chart.js";
import {
  readReleaseCache, renderRelease, loadRelease, startReleaseSync, copyCommand
} from "./release.js";
import { setDocumentLanguage, bindLanguage } from "./ui.js";
import { chartState, placeChartTip, hideChartTip } from "./chart.js";

function bindChartHover() {
  heroCanvas.addEventListener("pointermove", (event) => {
    if (!chartState.heroGeometry || !state.hero.samples.length) return;
    const rect = heroCanvas.getBoundingClientRect();
    const x = event.clientX - rect.left;
    let nearest = 0;
    let best = Infinity;
    chartState.heroGeometry.points.forEach((point, index) => {
      const distance = Math.abs(point.x - x);
      if (distance < best) {
        best = distance;
        nearest = index;
      }
    });
    chartState.heroHoverIndex = nearest;
    const point = chartState.heroGeometry.points[nearest];
    placeChartTip(heroChartTip, point.x, chartState.heroGeometry.width, Math.round(point.value) + " °C");
    drawHeroChart();
  });
  heroCanvas.addEventListener("pointerleave", () => {
    chartState.heroHoverIndex = null;
    hideChartTip(heroChartTip);
    drawHeroChart();
  });

  stressCanvas.addEventListener("pointermove", (event) => {
    if (!chartState.stressGeometry || !chartState.stressGeometry.points.length) return;
    const rect = stressCanvas.getBoundingClientRect();
    const x = event.clientX - rect.left;
    let nearest = null;
    let best = Infinity;
    chartState.stressGeometry.points.forEach((point) => {
      const distance = Math.abs(point.x - x);
      if (distance < best) {
        best = distance;
        nearest = point;
      }
    });
    if (!nearest) return;
    chartState.stressHoverSample = nearest.sample;
    placeChartTip(
      stressChartTip,
      nearest.x,
      chartState.stressGeometry.width,
      formatElapsed(nearest.sample.t) + " · " + Math.round(nearest.sample.temp) + " °C · " + Math.round(nearest.sample.power) + " W"
    );
    drawStressChart();
  });
  stressCanvas.addEventListener("pointerleave", () => {
    chartState.stressHoverSample = null;
    hideChartTip(stressChartTip);
    drawStressChart();
  });
}


function bindInteractions() {
  heroSwitch.addEventListener("click", () => {
    setHeroCooling(!state.hero.cooling);
    drawHeroChart();
  });

  if (heroWindowSelect) {
    heroWindowSelect.addEventListener("change", () => {
      const minutes = Number(heroWindowSelect.value);
      state.hero.windowMinutes = [1, 5, 10, 30, 60].includes(minutes) ? minutes : 1;
      chartState.heroHoverIndex = null;
      hideChartTip(heroChartTip);
      drawHeroChart();
    });
  }

  const windowTools = $(".app-window-icons");
  if (windowTools) windowTools.addEventListener("click", () => closePopover(false));

  ramButton.addEventListener("click", optimizeRam);

  stressStart.addEventListener("click", () => {
    if (state.stress.running) stopStress();
    else startStress();
  });

  stressToggle.addEventListener("click", toggleStressCooling);

  copyButton.addEventListener("click", copyCommand);
  bindTooltips();
  bindChartHover();
}


function initialize() {
  applyDeviceScenario();
  seedHeroSamples();
  applyStressProfile(null, true);
  bindLanguage();
  bindInteractions();

  let storedLanguage = "en";
  try {
    const stored = localStorage.getItem("cryotray-docs-lang");
    if (stored === "ar" || stored === "en") storedLanguage = stored;
  } catch {
    // Default to English.
  }

  const cachedRelease = readReleaseCache();
  state.release = cachedRelease ? cachedRelease.release : SEED_RELEASE;
  state.releaseStatus = "checking";
  renderRelease();
  setDocumentLanguage(storedLanguage);
  setHeroCooling(false);
  setStressState("ready");
  renderRam();
  renderScenario();
  renderTooltips();
  renderStressUI();
  drawHeroChart();
  drawGpuSpark();
  drawStressChart();
  startStress(true);
  loadRelease();
  startReleaseSync();

  let previousTime = performance.now();
  function frame(now) {
    const dt = Math.min(0.12, Math.max(0, (now - previousTime) / 1000));
    previousTime = now;
    if (!document.hidden) {
      updateHero(dt);
      updateStress(dt);
    }
    window.requestAnimationFrame(frame);
  }
  window.requestAnimationFrame(frame);

  if ("ResizeObserver" in window) {
    const observer = new ResizeObserver(() => {
      chartState.heroHoverIndex = null;
      chartState.stressHoverSample = null;
      hideChartTip(heroChartTip);
      hideChartTip(stressChartTip);
      drawHeroChart();
      drawGpuSpark();
      drawStressChart();
      renderTooltips();
      repositionActivePopover();
    });
    observer.observe(heroCanvas);
    observer.observe(gpuSparkCanvas);
    observer.observe(stressCanvas);
  } else {
    window.addEventListener("resize", () => {
      chartState.heroHoverIndex = null;
      chartState.stressHoverSample = null;
      hideChartTip(heroChartTip);
      hideChartTip(stressChartTip);
      drawHeroChart();
      drawGpuSpark();
      drawStressChart();
      repositionActivePopover();
    });
  }

  reduceMotion.addEventListener("change", () => {
    drawHeroChart();
    drawStressChart();
  });
}

initialize();

