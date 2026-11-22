/* Stress chart frame, target lines, two-color history, and hover marker. */
import { state, t, stressCanvas } from "./core.js";
import { chartState, fitCanvas } from "./chart.js";

function drawStressChart() {
  const fit = fitCanvas(stressCanvas);
  if (!fit) return;
  const { context: ctx, width, height } = fit;
  ctx.clearRect(0, 0, width, height);

  const plot = {
    left: 42,
    right: 14,
    top: 16,
    bottom: 30
  };
  const plotWidth = Math.max(1, width - plot.left - plot.right);
  const plotHeight = Math.max(1, height - plot.top - plot.bottom);
  const targets = state.stress.targets || { offTemp: 71, onTemp: 54 };
  const minTemp = 30;
  const maxTemp = Math.max(80, Math.ceil((targets.offTemp + 6) / 10) * 10);
  const maxTime = Math.max(60, Math.ceil(state.stress.elapsed / 30) * 30);
  const yFor = (value) => plot.top + (1 - (value - minTemp) / (maxTemp - minTemp)) * plotHeight;
  const xFor = (seconds) => plot.left + (seconds / maxTime) * plotWidth;

  ctx.font = '9px "Segoe UI", Tahoma, Arial, sans-serif';
  ctx.textBaseline = "middle";

  for (let temp = 40; temp < maxTemp; temp += 10) {
    const y = yFor(temp) + 0.5;
    ctx.beginPath();
    ctx.moveTo(plot.left, y);
    ctx.lineTo(width - plot.right, y);
    ctx.strokeStyle = "#333333";
    ctx.lineWidth = 1;
    ctx.stroke();
    ctx.fillStyle = "#888888";
    ctx.textAlign = "right";
    ctx.fillText(String(temp), plot.left - 7, y);
  }

  ctx.textAlign = "center";
  ctx.textBaseline = "top";
  for (let index = 0; index <= 4; index += 1) {
    const seconds = (maxTime / 4) * index;
    const x = xFor(seconds);
    ctx.beginPath();
    ctx.moveTo(x + 0.5, plot.top);
    ctx.lineTo(x + 0.5, height - plot.bottom);
    ctx.strokeStyle = "#2c2c2c";
    ctx.lineWidth = 1;
    ctx.stroke();
    ctx.fillStyle = "#888888";
    ctx.fillText(Math.round(seconds) + "s", x, height - plot.bottom + 8);
  }

  ctx.setLineDash([4, 4]);
  ctx.lineWidth = 1;
  ctx.strokeStyle = "rgba(229, 152, 126, 0.44)";
  ctx.beginPath();
  ctx.moveTo(plot.left, yFor(targets.offTemp));
  ctx.lineTo(width - plot.right, yFor(targets.offTemp));
  ctx.stroke();
  ctx.strokeStyle = "rgba(43, 136, 216, 0.56)";
  ctx.beginPath();
  ctx.moveTo(plot.left, yFor(targets.onTemp));
  ctx.lineTo(width - plot.right, yFor(targets.onTemp));
  ctx.stroke();
  ctx.setLineDash([]);

  if (!state.stress.samples.length) {
    chartState.stressGeometry = { points: [], plot, width, height, maxTime };
    ctx.fillStyle = "#888888";
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.font = '11px "Segoe UI", Tahoma, Arial, sans-serif';
    ctx.fillText(t("stress.chartEmpty"), plot.left + plotWidth / 2, plot.top + plotHeight / 2);
    return;
  }

  const visible = state.stress.samples.filter((sample) => sample.t <= maxTime);
  const points = visible.map((sample) => ({
    x: xFor(sample.t),
    y: yFor(sample.temp),
    sample
  }));
  chartState.stressGeometry = { points, plot, width, height, maxTime };
  for (let index = 1; index < visible.length; index += 1) {
    const previous = visible[index - 1];
    const current = visible[index];
    ctx.beginPath();
    ctx.moveTo(xFor(previous.t), yFor(previous.temp));
    ctx.lineTo(xFor(current.t), yFor(current.temp));
    ctx.strokeStyle = current.cooling ? "#2b88d8" : "#e07d62";
    ctx.lineWidth = 1.8;
    ctx.lineJoin = "round";
    ctx.lineCap = "round";
    ctx.stroke();
  }

  const last = visible[visible.length - 1];
  if (last) {
    ctx.beginPath();
    ctx.arc(xFor(last.t), yFor(last.temp), 2.8, 0, Math.PI * 2);
    ctx.fillStyle = last.cooling ? "#76c6ed" : "#e5987e";
    ctx.fill();
  }

  if (chartState.stressHoverSample && visible.includes(chartState.stressHoverSample)) {
    const x = xFor(chartState.stressHoverSample.t);
    const y = yFor(chartState.stressHoverSample.temp);
    ctx.beginPath();
    ctx.moveTo(x + 0.5, plot.top);
    ctx.lineTo(x + 0.5, height - plot.bottom);
    ctx.strokeStyle = "rgba(255, 255, 255, 0.22)";
    ctx.lineWidth = 1;
    ctx.stroke();
    ctx.beginPath();
    ctx.arc(x, y, 3, 0, Math.PI * 2);
    ctx.fillStyle = "#141414";
    ctx.fill();
    ctx.strokeStyle = chartState.stressHoverSample.cooling ? "#76c6ed" : "#e5987e";
    ctx.lineWidth = 1.4;
    ctx.stroke();
  }
}



export { drawStressChart };
