/* Shared state, DOM references, translations, and formatting helpers. */
import { I18N } from "./i18n.js";
import { SEED_RELEASE } from "./config.js";

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => Array.from(root.querySelectorAll(selector));
const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)");

const state = {
  lang: "en",
  release: SEED_RELEASE,
  releaseStatus: "checking",
  hero: {
    cooling: false,
    cpuName: "AMD Ryzen 7 5800X",
    temp: 53,
    displayTemp: 53,
    lastDisplayTemp: 53,
    clock: 3.78,
    maxClock: 3.8,
    cores: 12,
    coreThermals: [],
    coreLift: 0,
    power: 36,
    gpuTemp: 61,
    gpuDisplayTemp: 61,
    gpuDisplayLast: 61,
    gpuName: "Demo GPU",
    gpuLoad: 38,
    gpuPower: 30,
    gpuFan: 0,
    hotspot: 70,
    vramTemp: 60,
    ssdTemp: 42,
    ssdTempE: 44,
    ssdDisplayC: 42,
    ssdDisplayE: 44,
    ssdDisplayLastC: 42,
    ssdDisplayLastE: 44,
    ssdHealth: 98,
    ssdHealthE: 99,
    deviceProfile: null,
    windowMinutes: 1,
    samples: [],
    sampleModes: [],
    sensorTimer: 0,
    wobblePhase: 0,
    chartDirty: true,
    gpuSamples: [],
    gpuChartDirty: true,
    fanAngle: 0,
    fanSpeed: 14,
    ramTotal: 15.9,
    ramInstalled: 16,
    ramUsed: 8.9,
    ramTarget: 8.9,
    ramPercent: 56,
    ramTargetPercent: 56,
    ramReserved: 0.5,
    ramStandby: 4.7,
    ramStandbyTarget: 4.7,
    ramModified: 0.6,
    ramModifiedTarget: 0.6,
    ramFreedSize: 0,
    ramBusy: false,
    ramOptimized: false,
    ramStatus: "",
    tooltipTimer: 0
  },
  stress: {
    running: false,
    cooling: false,
    started: false,
    autoMode: false,
    autoCooled: false,
    previewComplete: false,
    elapsed: 0,
    temp: 43,
    displayTemp: 43,
    lastDisplayTemp: 43,
    speed: 3.2,
    power: 18,
    peak: 43,
    peakTime: 0,
    sensorTimer: 0,
    phase: 0,
    load: 0,
    loadMax: 1,
    ramp: 2.4,
    profile: null,
    targets: null,
    idle: { temp: 43, speed: 3.2, power: 18 },
    samples: [],
    chartSummaryTimer: 0
  }
};

const appPreview = $("#app-preview");
const heroSwitch = $("#hero-cooling");
const heroSwitchState = $("#hero-cooling-state");
const heroFanRotor = $("#fan-rotor");
const heroCanvas = $("#hero-chart");
const gpuSparkCanvas = $("#gpu-spark");
const heroWindowSelect = $("#hero-window");
const stressCanvas = $("#stress-chart");
const stressStart = $("#stress-start");
const stressToggle = $("#stress-cooling");
const stressState = $("#stress-state");
const stressCoolingLabel = $("#stress-cooling-state");
const ramButton = $("#ram-optimize");
const ramStatus = $("#ram-status");
const copyButton = $("#copy-checksum");
const copyStatus = $("#copy-status");
const previewScenario = $("#preview-scenario");
const cpuPopGrid = $("#cpu-pop-grid");
const stressProfileLabel = $("#stress-profile");
const stressPeak = $("#stress-peak");
const stressPower = $("#stress-power");
const stressAvgTemp = $("#stress-avg-temp");
const stressSamples = $("#stress-samples");
const stressPeakTime = $("#stress-peak-time");
const stressLoad = $("#stress-load");
const stressLoadFill = $("#stress-load-fill");
const heroChartTip = $("#hero-chart-tip");
const stressChartTip = $("#stress-chart-tip");


function t(key, vars) {
  const dictionary = I18N[state.lang] || I18N.en;
  let value = dictionary[key];
  if (value === undefined) value = I18N.en[key];
  if (value === undefined) return key;
  if (vars) {
    for (const [name, replacement] of Object.entries(vars)) {
      value = value.split("{" + name + "}").join(String(replacement));
    }
  }
  return value;
}

function setText(element, value) {
  if (element && element.textContent !== value) element.textContent = value;
}

function clamp(value, min, max) {
  return Math.min(max, Math.max(min, value));
}

function randomBetween(min, max) {
  return min + Math.random() * (max - min);
}

function randomInt(min, max) {
  return Math.floor(randomBetween(min, max + 1));
}

function pick(list) {
  return list[Math.floor(Math.random() * list.length)];
}

function roundTo(value, digits = 1) {
  const factor = 10 ** digits;
  return Math.round(value * factor) / factor;
}

function formatGb(value) {
  return value.toFixed(1) + " GB";
}

function alignCoreTemps(temps, target) {
  if (!temps.length) return temps;
  const shift = Math.max(...temps) - target;
  return temps.map((value) => clamp(value - shift, 25, 100));
}


function hexToRgb(hex) {
  const value = hex.replace("#", "");
  return [
    parseInt(value.slice(0, 2), 16),
    parseInt(value.slice(2, 4), 16),
    parseInt(value.slice(4, 6), 16)
  ];
}

function rgba(rgb, alpha) {
  return "rgba(" + rgb[0] + ", " + rgb[1] + ", " + rgb[2] + ", " + alpha + ")";
}

function formatDate(iso) {
  const locale = state.lang === "ar" ? "ar-u-nu-latn" : "en-US";
  try {
    return new Intl.DateTimeFormat(locale, {
      dateStyle: "medium",
      timeZone: "UTC"
    }).format(new Date(iso));
  } catch {
    return iso.slice(0, 10);
  }
}

function formatSize(bytes) {
  const locale = state.lang === "ar" ? "ar-u-nu-latn" : "en-US";
  const amount = Number(bytes || 0) / 1000000;
  try {
    return new Intl.NumberFormat(locale, {
      minimumFractionDigits: 1,
      maximumFractionDigits: 1
    }).format(amount) + " MB";
  } catch {
    return amount.toFixed(1) + " MB";
  }
}

function formatElapsed(seconds) {
  const whole = Math.min(5999, Math.max(0, Math.floor(seconds)));
  const minutes = Math.floor(whole / 60).toString().padStart(2, "0");
  const remainder = (whole % 60).toString().padStart(2, "0");
  return minutes + ":" + remainder;
}

function signed(value, digits = 0) {
  const number = Number(value);
  const absolute = Math.abs(number).toFixed(digits);
  if (Math.abs(number) < 10 ** -digits / 2) return digits ? (0).toFixed(digits) : "0";
  return (number < 0 ? "-" : "+") + absolute;
}

function signedPercent(value, base) {
  if (!base) return "0%";
  const percent = Math.round((Math.abs(value) / Math.abs(base)) * 100);
  return (value < 0 ? "-" : value > 0 ? "+" : "") + percent + "%";
}


export {
  reduceMotion, $, $$, state,
  appPreview, heroSwitch, heroSwitchState, heroFanRotor, heroCanvas, gpuSparkCanvas,
  heroWindowSelect, stressCanvas, stressStart, stressToggle, stressState, stressCoolingLabel,
  ramButton, ramStatus, copyButton, copyStatus, previewScenario, cpuPopGrid,
  stressProfileLabel, stressPeak, stressPower, stressAvgTemp, stressSamples, stressPeakTime,
  stressLoad, stressLoadFill, heroChartTip, stressChartTip,
  t, setText, clamp, randomBetween, randomInt, pick, roundTo, formatGb,
  alignCoreTemps, hexToRgb, rgba, formatDate, formatSize, formatElapsed, signed, signedPercent
};
