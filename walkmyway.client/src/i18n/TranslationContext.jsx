import { createContext, useContext, useState } from 'react';
import de from './de.json';
import en from './en.json';

const translations = { de, en };
const TranslationContext = createContext(null);

export function TranslationProvider({ children }) {
    const [lang, setLangState] = useState(
        () => localStorage.getItem('wmw-lang') || (navigator.language?.startsWith('de') ? 'de' : 'en')
    );

    const setLang = (l) => {
        localStorage.setItem('wmw-lang', l);
        setLangState(l);
    };

    const t = (key, vars = {}) => {
        const keys = key.split('.');
        let val = translations[lang];
        for (const k of keys) val = val?.[k];
        if (val == null) {
            val = translations.en;
            for (const k of keys) val = val?.[k];
        }
        if (typeof val !== 'string') return key;
        return val.replace(/\{\{(\w+)\}\}/g, (_, k) => String(vars[k] ?? ''));
    };

    return (
        <TranslationContext.Provider value={{ t, lang, setLang }}>
            {children}
        </TranslationContext.Provider>
    );
}

export const useTranslation = () => useContext(TranslationContext);
