/* Shared demo data and endpoints. */
const RELEASE_API = "https://api.github.com/repos/ZerroDevs/CryoTray/releases/latest";
const RELEASE_VERSION_URL = "https://raw.githubusercontent.com/ZerroDevs/CryoTray/main/VERSION";
const RELEASE_CACHE_KEY = "cryotray-docs-release-v2";
const RELEASE_BLOCK_KEY = "cryotray-docs-release-blocked";
const RELEASE_API_FRESH_MS = 5 * 60 * 1000;
const RELEASE_POLL_MS = 2 * 60 * 1000;
const RELEASE_EVENT_THROTTLE_MS = 30 * 1000;
const STRESS_PREVIEW_TOGGLE_AT = 7;
const STRESS_PREVIEW_DURATION = 15;

const DEVICE_PROFILES = [
  {
    id: "ultrabook",
    nameKey: "scenario.ultrabook",
    cpus: [
      { name: "Intel Core Ultra 7 155H", cores: 16 },
      { name: "Intel Core Ultra 5 125H", cores: 14 },
      { name: "AMD Ryzen 7 7840U", cores: 8 },
      { name: "AMD Ryzen 7 7735U", cores: 8 }
    ],
    coreSpread: [3.2, 6.0],
    maxClock: [4.0, 4.7],
    offTemp: [50, 56],
    onTemp: [36, 41],
    offClockRatio: [0.78, 0.9],
    onClockRatio: [0.68, 0.8],
    offPower: [24, 34],
    onPower: [13, 20],
    gpuNames: ["Intel Iris Xe", "AMD Radeon 780M", "Intel Arc Graphics"],
    gpuPower: [8, 24],
    offGpu: [56, 64],
    onGpu: [50, 57],
    offGpuLoad: [28, 48],
    onGpuLoad: [20, 35],
    hotspot: [9, 15],
    ssdOff: [38, 44],
    ssdOn: [35, 41],
    health: [92, 100],
    ramTotals: [15.9, 31.9],
    ramUsage: [0.46, 0.68]
  },
  {
    id: "gaming",
    nameKey: "scenario.gaming",
    cpus: [
      { name: "Intel Core i9-14900HX", cores: 24 },
      { name: "Intel Core i7-14700HX", cores: 20 },
      { name: "AMD Ryzen 9 7945HX", cores: 16 },
      { name: "Intel Core i7-13650HX", cores: 14 }
    ],
    coreSpread: [4.0, 7.8],
    maxClock: [4.8, 5.4],
    offTemp: [58, 66],
    onTemp: [44, 50],
    offClockRatio: [0.82, 0.94],
    onClockRatio: [0.7, 0.82],
    offPower: [52, 72],
    onPower: [30, 42],
    gpuNames: ["GeForce RTX 4060 Laptop", "GeForce RTX 4070 Laptop", "Radeon RX 7600S"],
    gpuPower: [65, 120],
    offGpu: [66, 74],
    onGpu: [58, 65],
    offGpuLoad: [62, 88],
    onGpuLoad: [46, 68],
    hotspot: [12, 18],
    ssdOff: [44, 52],
    ssdOn: [41, 48],
    health: [91, 100],
    ramTotals: [31.9, 63.9],
    ramUsage: [0.5, 0.7]
  },
  {
    id: "creator",
    nameKey: "scenario.creator",
    cpus: [
      { name: "Intel Core i9-13980HX", cores: 24 },
      { name: "Intel Core Ultra 9 185H", cores: 16 },
      { name: "Intel Core i7-13700H", cores: 14 },
      { name: "AMD Ryzen 9 7940HS", cores: 8 }
    ],
    coreSpread: [3.8, 7.2],
    maxClock: [4.6, 5.2],
    offTemp: [56, 64],
    onTemp: [43, 49],
    offClockRatio: [0.82, 0.93],
    onClockRatio: [0.72, 0.83],
    offPower: [48, 66],
    onPower: [28, 40],
    gpuNames: ["GeForce RTX 4070 Laptop", "Radeon Pro 7600M", "Intel Arc A770M"],
    gpuPower: [50, 95],
    offGpu: [62, 70],
    onGpu: [55, 62],
    offGpuLoad: [50, 72],
    onGpuLoad: [38, 55],
    hotspot: [10, 16],
    ssdOff: [43, 50],
    ssdOn: [40, 47],
    health: [94, 100],
    ramTotals: [31.9, 63.9],
    ramUsage: [0.54, 0.74]
  },
  {
    id: "mini",
    nameKey: "scenario.mini",
    cpus: [
      { name: "AMD Ryzen 7 5800X", cores: 8 },
      { name: "AMD Ryzen 7 5700G", cores: 8 },
      { name: "Intel Core i7-12700", cores: 12 },
      { name: "AMD Ryzen 7 7840HS", cores: 8 },
      { name: "Intel Core i5-12400", cores: 6 }
    ],
    coreSpread: [3.2, 6.0],
    maxClock: [4.2, 4.9],
    offTemp: [48, 55],
    onTemp: [38, 44],
    offClockRatio: [0.78, 0.9],
    onClockRatio: [0.7, 0.8],
    offPower: [30, 42],
    onPower: [18, 26],
    gpuNames: ["Radeon 780M", "Intel Iris Xe", "Radeon RX 6400"],
    gpuPower: [16, 40],
    offGpu: [54, 62],
    onGpu: [49, 56],
    offGpuLoad: [25, 45],
    onGpuLoad: [18, 32],
    hotspot: [8, 14],
    ssdOff: [39, 45],
    ssdOn: [37, 42],
    health: [93, 100],
    ramTotals: [15.9, 31.9],
    ramUsage: [0.44, 0.66]
  },
  {
    id: "desktop",
    nameKey: "scenario.desktop",
    cpus: [
      { name: "AMD Ryzen 7 5800X", cores: 8 },
      { name: "AMD Ryzen 7 7800X3D", cores: 8 },
      { name: "Intel Core i7-13700K", cores: 16 },
      { name: "AMD Ryzen 9 7950X", cores: 16 },
      { name: "Intel Core i9-13900K", cores: 24 },
      { name: "Intel Core i9-14900K", cores: 24 }
    ],
    coreSpread: [4.0, 7.6],
    maxClock: [4.8, 5.6],
    offTemp: [54, 62],
    onTemp: [42, 48],
    offClockRatio: [0.84, 0.95],
    onClockRatio: [0.74, 0.85],
    offPower: [62, 86],
    onPower: [38, 52],
    gpuNames: ["GeForce RTX 4070", "Radeon RX 7800 XT", "GeForce RTX 4080"],
    gpuPower: [100, 190],
    offGpu: [62, 71],
    onGpu: [55, 62],
    offGpuLoad: [55, 80],
    onGpuLoad: [42, 60],
    hotspot: [9, 15],
    ssdOff: [42, 49],
    ssdOn: [39, 46],
    health: [95, 100],
    ramTotals: [31.9, 63.9],
    ramUsage: [0.42, 0.66]
  },
  {
    id: "workstation",
    nameKey: "scenario.workstation",
    cpus: [
      { name: "AMD Ryzen Threadripper 7960X", cores: 24 },
      { name: "AMD Ryzen Threadripper 7970X", cores: 32 },
      { name: "AMD Ryzen Threadripper PRO 5995WX", cores: 64 },
      { name: "Intel Xeon w9-3495X", cores: 56 }
    ],
    coreSpread: [4.5, 9.0],
    maxClock: [3.8, 4.6],
    offTemp: [58, 66],
    onTemp: [45, 51],
    offClockRatio: [0.85, 0.96],
    onClockRatio: [0.76, 0.86],
    offPower: [95, 140],
    onPower: [60, 85],
    gpuNames: ["RTX A4000", "RTX A5000", "Radeon Pro W7800"],
    gpuPower: [80, 170],
    offGpu: [64, 73],
    onGpu: [57, 64],
    offGpuLoad: [45, 70],
    onGpuLoad: [34, 52],
    hotspot: [10, 17],
    ssdOff: [44, 51],
    ssdOn: [41, 48],
    health: [96, 100],
    ramTotals: [63.9],
    ramUsage: [0.5, 0.72]
  }
];

const STRESS_PROFILES = [
  {
    id: "ultrabook",
    nameKey: "profile.ultrabook",
    threads: [12, 16],
    offTemp: [69, 75],
    onTemp: [49, 56],
    offPower: [44, 60],
    onPower: [26, 38],
    offSpeed: [3.4, 4.0],
    onSpeed: [2.7, 3.2],
    idleTemp: [40, 45]
  },
  {
    id: "gaming",
    nameKey: "profile.gaming",
    threads: [16, 24],
    offTemp: [76, 84],
    onTemp: [58, 66],
    offPower: [100, 136],
    onPower: [74, 100],
    offSpeed: [4.4, 5.2],
    onSpeed: [3.6, 4.3],
    idleTemp: [42, 47]
  },
  {
    id: "creator",
    nameKey: "profile.creator",
    threads: [16, 32],
    offTemp: [74, 82],
    onTemp: [57, 64],
    offPower: [86, 118],
    onPower: [62, 88],
    offSpeed: [4.1, 4.9],
    onSpeed: [3.3, 4.1],
    idleTemp: [41, 46]
  },
  {
    id: "mini",
    nameKey: "profile.mini",
    threads: [12, 16],
    offTemp: [68, 75],
    onTemp: [50, 57],
    offPower: [68, 94],
    onPower: [49, 68],
    offSpeed: [3.5, 4.2],
    onSpeed: [3.0, 3.6],
    idleTemp: [38, 43]
  },
  {
    id: "desktop",
    nameKey: "profile.desktop",
    threads: [16, 32],
    offTemp: [73, 82],
    onTemp: [55, 63],
    offPower: [112, 158],
    onPower: [84, 118],
    offSpeed: [4.3, 5.1],
    onSpeed: [3.5, 4.2],
    idleTemp: [40, 45]
  },
  {
    id: "workstation",
    nameKey: "profile.workstation",
    threads: [24, 64],
    offTemp: [70, 80],
    onTemp: [53, 61],
    offPower: [150, 230],
    onPower: [112, 170],
    offSpeed: [3.8, 4.6],
    onSpeed: [3.2, 3.9],
    idleTemp: [40, 45]
  }
];


const SEED_RELEASE = {
  tag: "v0.0.103",
  publishedAt: "2026-11-19T22:31:40Z",
  htmlUrl: "https://github.com/ZerroDevs/CryoTray/releases/tag/v0.0.103",
  setup: {
    name: "CryoTray-Setup-0.0.103.exe",
    size: 52548842,
    url: "https://github.com/ZerroDevs/CryoTray/releases/download/v0.0.103/CryoTray-Setup-0.0.103.exe",
    shaUrl: "https://github.com/ZerroDevs/CryoTray/releases/download/v0.0.103/CryoTray-Setup-0.0.103.exe.sha256"
  },
  zip: {
    name: "CryoTray-0.0.103-win-x64.zip",
    size: 78236212,
    url: "https://github.com/ZerroDevs/CryoTray/releases/download/v0.0.103/CryoTray-0.0.103-win-x64.zip",
    shaUrl: "https://github.com/ZerroDevs/CryoTray/releases/download/v0.0.103/CryoTray-0.0.103-win-x64.zip.sha256"
  }
};


export {
  RELEASE_API, RELEASE_VERSION_URL, RELEASE_CACHE_KEY, RELEASE_BLOCK_KEY,
  RELEASE_API_FRESH_MS, RELEASE_POLL_MS, RELEASE_EVENT_THROTTLE_MS,
  STRESS_PREVIEW_TOGGLE_AT, STRESS_PREVIEW_DURATION,
  DEVICE_PROFILES, STRESS_PROFILES, SEED_RELEASE
};
