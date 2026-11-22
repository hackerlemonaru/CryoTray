/* Standalone language and path handling for the 404 page. */
import { I18N } from "./i18n.js";

const STORAGE_KEY = "cryotray-docs-lang";
const buttons = Array.from(document.querySelectorAll(".lang-btn"));
let lang = "en";

function text(key) {
  const dictionary = I18N[lang] || I18N.en;
  return dictionary[key] ?? I18N.en[key] ?? key;
}

function applyLanguage(next) {
  lang = next === "ar" ? "ar" : "en";
  document.documentElement.lang = lang;
  document.documentElement.dir = lang === "ar" ? "rtl" : "ltr";
  document.title = text("notFound.metaTitle");

  document.querySelectorAll("[data-i18n]").forEach((element) => {
    element.textContent = text(element.dataset.i18n);
  });

  document.querySelectorAll("[data-i18n-aria]").forEach((element) => {
    element.setAttribute("aria-label", text(element.dataset.i18nAria));
  });

  buttons.forEach((button) => {
    const active = button.dataset.lang === lang;
    button.classList.toggle("is-active", active);
    button.setAttribute("aria-pressed", String(active));
  });

  try {
    localStorage.setItem(STORAGE_KEY, lang);
  } catch {
    // Language still applies for this page view.
  }
}

let saved = "en";
try {
  saved = localStorage.getItem(STORAGE_KEY) || "en";
} catch {
  // Use English when storage is unavailable.
}

applyLanguage(saved);

buttons.forEach((button) => {
  button.addEventListener("click", () => {
    if (button.dataset.lang && button.dataset.lang !== lang) {
      applyLanguage(button.dataset.lang);
    }
  });
});

const path = document.querySelector("#not-found-path");
if (path) path.textContent = location.pathname || "/";
