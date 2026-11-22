/* Canvas sizing, chart interaction state, and floating chart tips. */
import { clamp } from "./core.js";

const chartState = {
  heroGeometry: null,
  stressGeometry: null,
  heroHoverIndex: null,
  stressHoverSample: null
};

function placeChartTip(tip, x, width, text) {
  if (!tip) return;
  tip.textContent = text;
  const tipWidth = tip.offsetWidth || 90;
  const left = clamp(x - tipWidth / 2, 5, Math.max(5, width - tipWidth - 5));
  tip.style.left = Math.round(left) + "px";
  tip.style.right = "auto";
  tip.classList.add("is-open");
}

function hideChartTip(tip) {
  if (tip) tip.classList.remove("is-open");
}


function fitCanvas(canvas) {
  const rect = canvas.getBoundingClientRect();
  if (rect.width < 1 || rect.height < 1) return null;
  const dpr = Math.min(window.devicePixelRatio || 1, 2);
  const pixelWidth = Math.round(rect.width * dpr);
  const pixelHeight = Math.round(rect.height * dpr);
  if (canvas.width !== pixelWidth || canvas.height !== pixelHeight) {
    canvas.width = pixelWidth;
    canvas.height = pixelHeight;
  }
  const context = canvas.getContext("2d");
  context.setTransform(dpr, 0, 0, dpr, 0, 0);
  return { context, width: rect.width, height: rect.height };
}


export { chartState, fitCanvas, placeChartTip, hideChartTip };
