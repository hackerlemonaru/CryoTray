/* Language switching and the localized re-render pass. */
import { state, t, $, $$, heroCanvas, stressCanvas } from "./core.js";
import { renderHero, renderRam } from "./hero.js";
import { renderScenario } from "./device.js";
import { renderTooltips } from "./tooltips.js";
import { renderStressUI } from "./stress.js";
import { drawStressChart } from "./stress-chart.js";
import { renderRelease, updateCopyStatusFromState } from "./release.js";

function setDocumentLanguage(lang) {
  state.lang = lang;
  document.documentElement.lang = lang;
  document.documentElement.dir = lang === "ar" ? "rtl" : "ltr";
  document.title = t("meta.title");
  const description = $("#meta-description");
  if (description) description.setAttribute("content", t("meta.description"));

  $$("[data-i18n]").forEach((element) => {
    const value = t(element.dataset.i18n);
    if (element.textContent !== value) element.textContent = value;
  });

  $$("[data-i18n-aria]").forEach((element) => {
    element.setAttribute("aria-label", t(element.dataset.i18nAria));
  });

  $$(".lang-btn").forEach((button) => {
    const active = button.dataset.lang === lang;
    button.classList.toggle("is-active", active);
    button.setAttribute("aria-pressed", String(active));
  });

  try {
    localStorage.setItem("cryotray-docs-lang", lang);
  } catch {
    // Language still applies for this page view.
  }

  heroCanvas.setAttribute("aria-label", t("a11y.heroChart"));
  stressCanvas.setAttribute("aria-label", t("stress.chartAria"));
  renderHero();
  renderRam();
  renderScenario();
  renderTooltips();
  renderStressUI();
  renderRelease();
  updateCopyStatusFromState();
}

function bindLanguage() {
  $$(".lang-btn").forEach((button) => {
    button.addEventListener("click", () => {
      if (button.dataset.lang && button.dataset.lang !== state.lang) {
        setDocumentLanguage(button.dataset.lang);
        drawStressChart();
      }
    });
  });
}


export { setDocumentLanguage, bindLanguage };
