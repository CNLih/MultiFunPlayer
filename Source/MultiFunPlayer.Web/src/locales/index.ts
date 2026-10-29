import { en } from "./en";
import { zh } from "./zh";

export type Language = "en" | "zh";
export type LocaleSchema = typeof en;

const dictionaries: Record<Language, LocaleSchema> = { en, zh };

function detectInitialLanguage(): Language {
  try {
    const saved = localStorage.getItem("mfp_lang");
    if (saved === "en" || saved === "zh") return saved;
    if (typeof navigator !== "undefined" && navigator.language && navigator.language.toLowerCase().startsWith("zh")) {
      return "zh";
    }
  } catch { }
  return "en";
}

let activeLanguage: Language = detectInitialLanguage();

export function getLanguage(): Language {
  return activeLanguage;
}

export function setLanguage(lang: Language): void {
  activeLanguage = lang;
  try {
    localStorage.setItem("mfp_lang", lang);
    if (typeof document !== "undefined" && document.documentElement) {
      document.documentElement.lang = lang === "zh" ? "zh-CN" : "en";
    }
  } catch { }
}

function resolveNested(obj: any, keys: string[]): any {
  let curr = obj;
  for (const k of keys) {
    if (curr && typeof curr === "object" && k in curr) {
      curr = curr[k];
    } else {
      return undefined;
    }
  }
  return curr;
}

export function t(key: string, params?: Record<string, string | number>): string {
  const keys = key.split(".");
  const dict = dictionaries[activeLanguage] ?? en;
  let text = resolveNested(dict, keys);
  if (typeof text !== "string") {
    text = resolveNested(en, keys);
  }
  if (typeof text !== "string") {
    return key;
  }
  if (params) {
    for (const [paramKey, paramVal] of Object.entries(params)) {
      text = text.replace(new RegExp(`\\{${paramKey}\\}`, "g"), String(paramVal));
    }
  }
  return text;
}
