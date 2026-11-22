/* Hero chart history, weather-style fan chart, and GPU sparkline. */
import {
  state, heroCanvas, gpuSparkCanvas, clamp, randomBetween, hexToRgb, rgba
} from "./core.js";
import { chartState, fitCanvas } from "./chart.js";

function seedSampleModes(count, currentCooling) {
  const modes = [];
  for (let index = 0; index < count; index += 1) {
    const phase = index % 165;
    modes.push(phase >= 95 && phase < 140);
  }
  if (modes.length) modes[modes.length - 1] = Boolean(currentCooling);
  return modes;
}

function seedHeroSamples() {
  const hero = state.hero;
  hero.samples.length = 0;
  hero.sampleModes.length = 0;
  const base = (hero.displayTemp || hero.temp) + hero.coreLift;
  const phase = randomBetween(0, Math.PI * 2);
  let value = base + randomBetween(-1.2, 1.2);
  for (let index = 0; index < 3600; index += 1) {
    value += (base - value) * 0.035
      + randomBetween(-0.3, 0.3)
      + Math.sin(index * 0.047 + phase) * 0.05;
    value = clamp(value, base - 2.2, base + 2.2);
    hero.samples.push(value);
  }
  hero.samples[hero.samples.length - 1] = base;
  hero.sampleModes = seedSampleModes(hero.samples.length, hero.cooling);
  hero.lastDisplayTemp = Math.round(hero.displayTemp);
  hero.sensorTimer = 0;
  hero.chartDirty = true;

  hero.gpuSamples.length = 0;
  for (let index = 0; index < 48; index += 1) {
    hero.gpuSamples.push(clamp(hero.gpuLoad + randomBetween(-2.2, 2.2), 0, 100));
  }
  hero.gpuChartDirty = true;
}

function heroWindowData() {
  const count = Math.max(1, state.hero.windowMinutes * 60);
  return {
    samples: state.hero.samples.slice(-count),
    modes: state.hero.sampleModes.slice(-count)
  };
}


function compressChartSamples(samples, modes = [], maxPoints = 240) {
  if (samples.length <= maxPoints) {
    return samples.map((value, index) => ({ value, cooling: Boolean(modes[index]) }));
  }
  const bucketSize = Math.ceil(samples.length / maxPoints);
  const result = [];
  for (let index = 0; index < samples.length; index += bucketSize) {
    let sum = 0;
    let count = 0;
    let coolingVotes = 0;
    const end = Math.min(index + bucketSize, samples.length);
    for (let inner = index; inner < end; inner += 1) {
      sum += samples[inner];
      count += 1;
      if (modes[inner]) coolingVotes += 1;
    }
    result.push({ value: sum / count, cooling: coolingVotes > count / 2 });
  }
  return result;
}

function drawHeroChart() {
  const fit = fitCanvas(heroCanvas);
  if (!fit) return;
  const { context: ctx, width, height } = fit;
  ctx.clearRect(0, 0, width, height);

  ctx.strokeStyle = state.hero.cooling ? "#23485c" : "#2c2c2c";
  ctx.lineWidth = 1;
  for (let row = 1; row < 4; row += 1) {
    const y = (height / 4) * row + 0.5;
    ctx.beginPath();
    ctx.moveTo(0, y);
    ctx.lineTo(width, y);
    ctx.stroke();
  }
  for (let column = 1; column < 4; column += 1) {
    const x = (width / 4) * column + 0.5;
    ctx.beginPath();
    ctx.moveTo(x, 0);
    ctx.lineTo(x, height);
    ctx.stroke();
  }

  const series = heroWindowData();
  const samples = compressChartSamples(series.samples, series.modes);
  if (!samples.length) {
    chartState.heroGeometry = null;
    state.hero.chartDirty = false;
    return;
  }
  let min = Infinity;
  let max = -Infinity;
  for (const sample of samples) {
    if (sample.value < min) min = sample.value;
    if (sample.value > max) max = sample.value;
  }
  if (max - min < 6) {
    const midpoint = (max + min) / 2;
    min = midpoint - 3;
    max = midpoint + 3;
  }
  const range = Math.max(1, max - min);
  const paddingX = 4;
  const paddingY = 6;
  const plotWidth = Math.max(1, width - paddingX * 2);
  const plotHeight = Math.max(1, height - paddingY * 2);
  const points = samples.map((sample, index) => ({
    x: paddingX + (index / Math.max(1, samples.length - 1)) * plotWidth,
    y: paddingY + (1 - (sample.value - min) / range) * plotHeight,
    value: sample.value,
    cooling: sample.cooling,
    ageSeconds: samples.length - 1 - index
  }));

  chartState.heroGeometry = { points, width, height };
  const onColor = hexToRgb("#2b88d8");
  const offColor = hexToRgb("#e07d62");
  const pointColor = (point) => rgba(point.cooling ? onColor : offColor, 1);

  for (let index = 0; index < points.length - 1; index += 1) {
    const start = points[index];
    const end = points[index + 1];
    const color = end.cooling ? onColor : offColor;
    ctx.beginPath();
    ctx.moveTo(start.x, height - 1);
    ctx.lineTo(start.x, start.y);
    ctx.lineTo(end.x, end.y);
    ctx.lineTo(end.x, height - 1);
    ctx.closePath();
    ctx.fillStyle = rgba(color, 0.1);
    ctx.fill();
  }

  ctx.lineWidth = 1.5;
  ctx.lineJoin = "round";
  ctx.lineCap = "round";
  for (let index = 0; index < points.length - 1; index += 1) {
    const start = points[index];
    const end = points[index + 1];
    ctx.beginPath();
    ctx.moveTo(start.x, start.y);
    ctx.lineTo(end.x, end.y);
    ctx.strokeStyle = pointColor(end);
    ctx.stroke();
  }

  const last = points[points.length - 1];
  ctx.beginPath();
  ctx.arc(last.x, last.y, 2.4, 0, Math.PI * 2);
  ctx.fillStyle = pointColor(last);
  ctx.fill();

  if (chartState.heroHoverIndex != null && points[chartState.heroHoverIndex]) {
    const hover = points[chartState.heroHoverIndex];
    ctx.beginPath();
    ctx.moveTo(hover.x + 0.5, 0);
    ctx.lineTo(hover.x + 0.5, height);
    ctx.strokeStyle = "rgba(255, 255, 255, 0.18)";
    ctx.lineWidth = 1;
    ctx.stroke();
    ctx.beginPath();
    ctx.arc(hover.x, hover.y, 2.6, 0, Math.PI * 2);
    ctx.fillStyle = pointColor(hover);
    ctx.fill();
  }
  state.hero.chartDirty = false;
}

function drawGpuSpark() {
  const fit = fitCanvas(gpuSparkCanvas);
  if (!fit) return;
  const { context: ctx, width, height } = fit;
  ctx.clearRect(0, 0, width, height);
  ctx.strokeStyle = "#292929";
  ctx.lineWidth = 1;
  for (let row = 1; row < 3; row += 1) {
    const y = (height / 3) * row + 0.5;
    ctx.beginPath();
    ctx.moveTo(0, y);
    ctx.lineTo(width, y);
    ctx.stroke();
  }

  const samples = state.hero.gpuSamples;
  if (!samples.length) {
    state.hero.gpuChartDirty = false;
    return;
  }
  let min = Math.min(...samples);
  let max = Math.max(...samples);
  if (max - min < 12) {
    const midpoint = (max + min) / 2;
    min = midpoint - 6;
    max = midpoint + 6;
  }
  const paddingY = 3;
  const plotHeight = Math.max(1, height - paddingY * 2);
  const points = samples.map((sample, index) => ({
    x: (index / Math.max(1, samples.length - 1)) * width,
    y: paddingY + (1 - (sample - min) / Math.max(1, max - min)) * plotHeight
  }));
  const gradient = ctx.createLinearGradient(0, 0, 0, height);
  gradient.addColorStop(0, "rgba(124, 179, 66, 0.42)");
  gradient.addColorStop(1, "rgba(124, 179, 66, 0.04)");
  ctx.beginPath();
  ctx.moveTo(0, height);
  points.forEach((point) => ctx.lineTo(point.x, point.y));
  ctx.lineTo(width, height);
  ctx.closePath();
  ctx.fillStyle = gradient;
  ctx.fill();
  ctx.beginPath();
  points.forEach((point, index) => {
    if (index === 0) ctx.moveTo(point.x, point.y);
    else ctx.lineTo(point.x, point.y);
  });
  ctx.strokeStyle = "#7cb342";
  ctx.lineWidth = 1.4;
  ctx.lineJoin = "round";
  ctx.lineCap = "round";
  ctx.stroke();
  state.hero.gpuChartDirty = false;
}


export { seedHeroSamples, drawHeroChart, drawGpuSpark };
