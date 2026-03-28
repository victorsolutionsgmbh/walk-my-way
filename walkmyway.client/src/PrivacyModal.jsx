import { useEffect } from 'react';
import { useTranslation } from './i18n/TranslationContext.jsx';

export default function PrivacyModal({ onClose }) {
    const { t } = useTranslation();

    useEffect(() => {
        const onKey = (e) => { if (e.key === 'Escape') onClose(); };
        document.addEventListener('keydown', onKey);
        return () => document.removeEventListener('keydown', onKey);
    }, [onClose]);

    return (
        <div className="wmw-modal-overlay" onClick={onClose}>
            <div className="wmw-modal" onClick={e => e.stopPropagation()} role="dialog" aria-modal="true">
                <div className="wmw-modal-header">
                    <h2 className="wmw-modal-title">{t('privacy.title')}</h2>
                    <button className="wmw-modal-close" onClick={onClose} aria-label={t('privacy.close')}>✕</button>
                </div>

                <div className="wmw-modal-body">
                    <section className="wmw-modal-section">
                        <h3>{t('privacy.license_section')}</h3>
                        <p>{t('privacy.license_body')}</p>
                    </section>

                    <section className="wmw-modal-section">
                        <h3>{t('privacy.cloudflare_section')}</h3>
                        <p>{t('privacy.cloudflare_body')}</p>
                    </section>

                    <section className="wmw-modal-section">
                        <h3>{t('privacy.tracking_section')}</h3>
                        <p>{t('privacy.tracking_body')}</p>
                    </section>

                    <section className="wmw-modal-section">
                        <h3>{t('privacy.data_section')}</h3>
                        <p>{t('privacy.data_body')}</p>
                    </section>
                </div>
            </div>
        </div>
    );
}
