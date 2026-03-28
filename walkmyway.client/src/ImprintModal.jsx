import { useEffect } from 'react';
import { useTranslation } from './i18n/TranslationContext.jsx';

export default function ImprintModal({ onClose }) {
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
                    <h2 className="wmw-modal-title">{t('imprint.title')}</h2>
                    <button className="wmw-modal-close" onClick={onClose} aria-label={t('imprint.close')}>✕</button>
                </div>

                <div className="wmw-modal-body">
                    <section className="wmw-modal-section">
                        <h3>{t('imprint.operator_section')}</h3>
                        <p>
                            <strong>{t('imprint.company')}</strong><br />
                            {t('imprint.owner')}<br />
                            {t('imprint.street')}<br />
                            {t('imprint.city')}<br />
                            {t('imprint.country')}
                        </p>
                    </section>

                    <section className="wmw-modal-section">
                        <h3>{t('imprint.contact_section')}</h3>
                        <p>
                            {t('imprint.email_label')}:{' '}
                            <a href={`mailto:${t('imprint.email')}`}>{t('imprint.email')}</a>
                        </p>
                    </section>

                    <section className="wmw-modal-section">
                        <h3>{t('imprint.vat_section')}</h3>
                        <p>{t('imprint.vat')}</p>
                    </section>

                    <section className="wmw-modal-section">
                        <h3>{t('imprint.register_section')}</h3>
                        <p>{t('imprint.register')}</p>
                    </section>

                    <section className="wmw-modal-section">
                        <h3>{t('imprint.profession_section')}</h3>
                        <p>
                            {t('imprint.chamber')}<br />
                            {t('imprint.trade_group')}<br />
                            {t('imprint.profession')}<br />
                            {t('imprint.court')}
                        </p>
                    </section>

                    <section className="wmw-modal-section">
                        <h3>{t('imprint.liability_content_section')}</h3>
                        <p>{t('imprint.liability_content_p1')}</p>
                        <p>{t('imprint.liability_content_p2')}</p>
                    </section>

                    <section className="wmw-modal-section">
                        <h3>{t('imprint.liability_links_section')}</h3>
                        <p>{t('imprint.liability_links_p1')}</p>
                        <p>{t('imprint.liability_links_p2')}</p>
                    </section>

                    <section className="wmw-modal-section">
                        <h3>{t('imprint.copyright_section')}</h3>
                        <p>{t('imprint.copyright_p1')}</p>
                        <p>{t('imprint.copyright_p2')}</p>
                    </section>
                </div>
            </div>
        </div>
    );
}
