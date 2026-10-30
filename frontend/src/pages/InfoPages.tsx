import { Link } from 'react-router-dom'
import { Breadcrumbs } from '../components/Breadcrumbs'
import { CONTACT_EMAIL } from '../content/contact'
import { participationSteps } from '../content/steps'
import { usePlatform } from '../platform/context'
import { useTitle } from '../lib/useTitle'
import { ContactForm } from './ContactForm'
import './ListPage.css'
import './InfoPages.css'

export function HowToPage() {
  const { platformName } = usePlatform()
  useTitle('Necə iştirak etməli?')
  return (
    <div className="container list-page">
      <Breadcrumbs items={[{ label: platformName, to: '/' }, { label: 'Necə iştirak etməli?' }]} />
      <header className="list-page__head">
        <h1>Necə iştirak etməli?</h1>
        <p className="lede">{platformName}-da bütün müsabiqələrə eyni yolla qatılırsınız. Bir hesab kifayətdir.</p>
      </header>

      <ol role="list" className="howto">
        {participationSteps(platformName).map((s, i) => (
          <li key={s.title} className="howto__step">
            <span className="howto__n tabular" aria-hidden="true">
              {i + 1}
            </span>
            <div>
              <h2>{s.title}</h2>
              <p>{s.text}</p>
            </div>
          </li>
        ))}
      </ol>

      <div className="howto__cta">
        <Link to="/competitions" className="btn btn--primary btn--lg">
          Müsabiqələrə bax
        </Link>
        <Link to="/register" className="btn btn--lg">
          Hesab yarat
        </Link>
      </div>
    </div>
  )
}

/**
 * Official legal text has not been provided yet. This page shows the agreed structure and says so plainly — it never
 * invents terms. The organisation's approved text replaces the pending sections as-is.
 */
export function TermsPage() {
  const { platformName } = usePlatform()
  useTitle('İştirak şərtləri')
  return (
    <div className="container list-page legal">
      <Breadcrumbs items={[{ label: platformName, to: '/' }, { label: 'İştirak şərtləri' }]} />
      <header className="list-page__head">
        <h1>İştirak şərtləri</h1>
        <p className="lede">Platformanın ümumi şərtlərinin rəsmi mətni hazırlanır və təşkilat tərəfindən təsdiqləndikdən sonra burada dərc olunacaq.</p>
      </header>
      <div className="legal__notice" role="note">
        <p>
          <strong>Rəsmi mətn hələ dərc olunmayıb.</strong> Hər müsabiqənin öz qaydaları, tələbləri və tarixləri (əgər
          müəyyən edilibsə) həmin müsabiqənin səhifəsində göstərilir.
        </p>
      </div>
      <ol className="legal__sections">
        {[
          'Ümumi müddəalar',
          'Hesab və qeydiyyat',
          'Təkliflərin göndərilməsi',
          'Müəllif hüquqları və işlərdən istifadə',
          'Nəticələr və qalereya',
          'Məsuliyyət',
          'Əlaqə',
        ].map((title) => (
          <li key={title}>
            <h2>{title}</h2>
            <p className="legal__pending">Mətn təsdiqlənəndən sonra əlavə olunacaq.</p>
          </li>
        ))}
      </ol>
      <p className="muted">
        Qeydiyyat zamanı «İştirak şərtləri ilə razıyam» seçimi məcburidir; sistem yalnız razılığın verildiyi vaxtı yadda saxlayır.
        Məlumatların necə işləndiyinin texniki təsviri: <Link to="/privacy">Məxfilik</Link>.
      </p>
    </div>
  )
}

/**
 * The official privacy notice is pending. What is here is a factual description of how the software handles data,
 * so participants can see it today; it is labelled as such and is not a legal document.
 */
export function PrivacyPage() {
  const { platformName } = usePlatform()
  useTitle('Məxfilik')
  return (
    <div className="container list-page legal">
      <Breadcrumbs items={[{ label: platformName, to: '/' }, { label: 'Məxfilik' }]} />
      <header className="list-page__head">
        <h1>Məxfilik</h1>
        <p className="lede">Rəsmi məxfilik siyasəti təşkilat tərəfindən təsdiqləndikdən sonra burada dərc olunacaq.</p>
      </header>
      <div className="legal__notice" role="note">
        <p>
          <strong>Aşağıdakı mətn hüquqi sənəd deyil.</strong> Bu, platformanın məlumatlarla texniki olaraq necə işlədiyinin təsviridir.
        </p>
      </div>
      <div className="legal__facts">
        <section>
          <h2>Hesab</h2>
          <ul>
            <li>Ad, soyad, telefon və e-mail hesabınızda saxlanılır. Bunlar yalnız sizə və platformanın administratorlarına görünür.</li>
            <li>Şifrə açıq şəkildə saxlanılmır — yalnız onun geri qaytarılmayan heş-dəyəri.</li>
            <li>Giriş sessiyası brauzerdə yalnız server tərəfindən oxunan (HttpOnly) cookie ilə saxlanılır. Şifrə yeniləndikdə bütün cihazlardakı girişlər dərhal bağlanır.</li>
          </ul>
        </section>
        <section>
          <h2>Təkliflər və fayllar</h2>
          <ul>
            <li>Qaralamalar yalnız sizə görünür. Göndərilmiş təkliflərə müsabiqəni idarə edən administratorlar baxır.</li>
            <li>Yüklədiyiniz fayllar olduğu kimi, dəyişdirilmədən və gizli saxlanılır; onları yalnız siz və administratorlar aça bilər.</li>
            <li>Administratorların daxili qeydləri və yoxlama statusları sizə və ictimaiyyətə göstərilmir.</li>
          </ul>
        </section>
        <section>
          <h2>Nəticələr dərc olunandan sonra</h2>
          <ul>
            <li>Nəticələr dərc olunana qədər heç bir təklif, qalib və ya fayl ictimai deyil.</li>
            <li>
              Dərcdən sonra qalereyada yalnız istinad nömrəsi, kateqoriya və müsabiqə administratorunun ictimai olaraq işarələdiyi
              sahələr görünür. Rədd edilmiş işlər göstərilmir.
            </li>
            <li>
              Şəkillər ictimaiyyətə orijinal kimi yox, serverin yenidən hazırladığı nüsxə kimi göstərilir: GPS koordinatları, cihaz,
              müəllif və digər metadata silinir. PDF sənədlərin özü ictimai olmur — yalnız birinci səhifəsinin şəkli göstərilə bilər.
            </li>
            <li>Adınız, e-mailiniz, telefonunuz və faylların orijinal adları ictimai göstərilmir.</li>
          </ul>
        </section>
        <section>
          <h2>E-mail</h2>
          <ul>
            <li>Platforma yalnız xidmət məktubları göndərir: şifrənin yenilənməsi linki və təklifin qəbul edildiyi barədə bildiriş.</li>
          </ul>
        </section>
      </div>
      <p className="muted">
        Ümumi şərtlər: <Link to="/terms">İştirak şərtləri</Link>.
      </p>
    </div>
  )
}

/**
 * "PRIZMA haqqında" — the platform in its own words: what it is, who may enter, and where the name comes from.
 * Reached from the footer; the home page carries a short version of the same thing with a link here.
 *
 * Every claim is either true today or written as an aim, because this page is the first thing a visitor reads
 * about the place: entry really is open to anyone, AI-made work really is accepted, and the shared, discussed
 * environment is stated as what the platform is working towards — never as a feature that already ships.
 */
export function AboutPage() {
  const { platformName } = usePlatform()
  useTitle(`${platformName} haqqında`)
  return (
    <div className="container list-page about">
      <Breadcrumbs items={[{ label: platformName, to: '/' }, { label: `${platformName} haqqında` }]} />
      <header className="list-page__head">
        <h1>{platformName} haqqında</h1>
        <p className="lede">“{platformName}”ya xoş gəlmisiniz!</p>
      </header>

      <div className="about__body">
        <p>
          {platformName} müxtəlif mövzularda mütəmadi olaraq müsabiqələrin keçirildiyi və hər kəsə açıq yaradıcı
          platformadır.
        </p>
        <p>
          Müsabiqələrdə yaş məhdudiyyəti qoyulmur. İştirakçının peşəkar rəssam və ya dizayner olması şərt deyil.
          Mövzuya uyğun orijinal ideyasını təqdim edən hər kəsin qalib olmaq və mükafat qazanmaq imkanı var.
        </p>
        <p>
          Süni intellekt vasitəsilə hazırlanmış təqdimatlar da qəbul edilir. Əsas olan yanaşmanın özünəməxsusluğu və
          ideyanın orijinallığıdır.
        </p>
        <p>
          {platformName} yaradıcı insanların bir-birinin işlərini kəşf edə bildiyi açıq mühit formalaşdırmağı
          hədəfləyir.
        </p>
        <p>
          “{platformName}” adını da məhz buna görə seçdik: bir işıq şüası prizmadan keçərkən müxtəlif rənglərə
          ayrıldığı kimi, fərqli baxışlar da eyni mövzuya müxtəlif və rəngarəng yanaşmalar ortaya çıxarır.
        </p>
        <p>
          Məqsədimiz sadəcə müsabiqələr keçirmək deyil. Zövqlü və yaradıcı insanların görüşdüyü, ideyaların
          paylaşıldığı və müzakirə olunduğu yeni yaradıcılıq mühiti formalaşdırmaqdır.
        </p>
        <p>Bu mənada {platformName}-ya qoşulan hər kəs artıq bu yaradıcı mühitin bir hissəsidir.</p>
        <p className="about__sign">Uğurlar!</p>
      </div>

      <div className="howto__cta">
        <Link to="/competitions" className="btn btn--primary btn--lg">
          Müsabiqələrə bax
        </Link>
        <Link to="/how-to-participate" className="btn btn--lg">
          Necə iştirak etməli?
        </Link>
      </div>
    </div>
  )
}

/** Where to write: the form, sent from the site itself, and beside it the address for the reader's own mail. */
export function ContactPage() {
  const { platformName } = usePlatform()
  useTitle('Əlaqə')
  return (
    <div className="container list-page contact">
      <Breadcrumbs items={[{ label: platformName, to: '/' }, { label: 'Əlaqə' }]} />
      <header className="list-page__head">
        <h1>Əlaqə</h1>
        <p className="lede">Sualınız, təklifiniz və ya müsabiqə ilə bağlı müraciətiniz varsa, bizə yazın.</p>
      </header>
      <div className="contact__layout">
        <ContactForm />
        <aside className="contact__aside" aria-labelledby="contact-mail-title">
          <h2 id="contact-mail-title">Və ya e-poçtla</h2>
          <p className="contact__address">
            {/* A narrow screen breaks the address before the "@", never inside a word. */}
            <a href={`mailto:${CONTACT_EMAIL}`}>
              {CONTACT_EMAIL.split('@')[0]}
              <wbr />@{CONTACT_EMAIL.split('@')[1]}
            </a>
          </p>
        </aside>
      </div>
    </div>
  )
}
