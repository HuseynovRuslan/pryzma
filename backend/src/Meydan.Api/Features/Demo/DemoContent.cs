using Meydan.Domain.Enums;

namespace Meydan.Api.Features.Demo;

/// <summary>
/// What the demo seeder creates. People, texts and dates are illustrative; the images come from ./demo-assets and are
/// mapped through its manifest.json. Nothing here is a business fact (no prizes, no real contact details).
/// </summary>
public static class DemoContent
{
    public sealed record Person(string Key, string FirstName, string LastName, string Email, int JoinedDaysAgo);

    /// <summary>Demo participants: .invalid addresses (never deliverable), no password, locked, never linked to QRLog.</summary>
    public static readonly Person[] People =
    [
        new("aysel", "Aysel", "Məmmədova", "aysel.mammadova@example.invalid", 58),
        new("murad", "Murad", "Əliyev", "murad.aliyev@example.invalid", 55),
        new("narmin", "Nərmin", "Hüseynli", "narmin.huseynli@example.invalid", 53),
        new("tural", "Tural", "Qasımov", "tural.qasimov@example.invalid", 51),
        new("leyla", "Leyla", "Rzayeva", "leyla.rzayeva@example.invalid", 49),
        new("orxan", "Orxan", "İsmayılov", "orxan.ismayilov@example.invalid", 47),
        new("nigar", "Nigar", "Əhmədova", "nigar.ahmadova@example.invalid", 44),
        new("elvin", "Elvin", "Həsənov", "elvin.hasanov@example.invalid", 42),
        new("samir", "Samir", "Məmmədli", "samir.mammadli@example.invalid", 40),
        new("zahra", "Zəhra", "Əliyeva", "zahra.aliyeva@example.invalid", 37),
        new("kamran", "Kamran", "Cəfərov", "kamran.cafarov@example.invalid", 35),
        new("aynur", "Aynur", "Mustafayeva", "aynur.mustafayeva@example.invalid", 33),
        new("rashad", "Rəşad", "Həsənli", "rashad.hasanli@example.invalid", 30),
        new("gunel", "Günel", "Quliyeva", "gunel.quliyeva@example.invalid", 28),
        new("farid", "Fərid", "Əliyev", "farid.aliyev@example.invalid", 26),
    ];

    /// <param name="CoverTextFreeFrom">
    /// Where the supplied cover's own lettering ends, as a fraction of its width: the cover is cropped to the part right
    /// of it (see <see cref="DemoArtwork.CropCover"/>). 0 keeps the cover whole.
    /// </param>
    public sealed record ProjectDef(
        string Slug, string Name, string ShortDescription, string Description, string AccentColor, int SortOrder,
        double CoverTextFreeFrom = 0);

    /// <summary>
    /// The manifest's "title" of a project cover equals the project's name. The supplied covers print that name and a
    /// tagline over their left ~44%; the crop keeps the right 52%.
    /// </summary>
    public static readonly ProjectDef[] Projects =
    [
        new("xezer-sahili", "Xəzər sahili",
            "Xəzər sahilinin təbiətini, şəhər həyatını və dəniz mədəniyyətini yaradıcı layihələrlə ifadə edən platforma.",
            "Xəzər sahili layihəsi dənizlə şəhərin görüşdüyü yeri — bulvarı, balıqçı kəndlərini, sahil boyu gəzinti yollarını — "
            + "rəssamların, dizaynerlərin və fotoqrafların gözü ilə təqdim edir. Müsabiqələr sahil həyatının gündəlik ritmini və "
            + "dənizin şəhər mədəniyyətindəki yerini yeni vizual dillə göstərməyə dəvət edir.",
            "#1F5F7A", 2, CoverTextFreeFrom: 0.48),
        new("yasil-baki", "Yaşıl Bakı",
            "Bakının yaşıl məkanlarını, bağlarını və ekoloji gələcəyini yaradıcı ideyalarla yenidən düşünən layihə.",
            "Yaşıl Bakı parkları, məhəllə bağlarını və şəhərin yaşıl gələcəyini mövzu seçir. Layihənin müsabiqələri daha yaşana "
            + "bilən, davamlı şəhər üçün ideyaları vizual kimlik, plakat və məkan həlləri şəklində toplamağa yönəlib.",
            "#3F6B45", 3, CoverTextFreeFrom: 0.48),
        new("abseron-irsi", "Abşeron irsi",
            "Abşeronun daş memarlığını, tarixi yaşayış məkanlarını və mədəni yaddaşını müasir yaradıcı baxışla təqdim edən layihə.",
            "Abşeron irsi yarımadanın qalalarını, karvansaralarını, qədim məhəllələrini və daşda qalan izləri sənədləşdirir. "
            + "Müsabiqələr bu irsi fotoqrafiya və dizayn vasitəsilə yenidən görməyə, gələcək nəsillər üçün qeydə almağa çağırır.",
            "#8A5A3C", 4, CoverTextFreeFrom: 0.48),
    ];

    public enum Phase { Open, Upcoming, Completed }

    public sealed record CompetitionDef(
        string Slug, string ProjectSlug, string Title, string ShortDescription, string Description, string Requirements,
        string FileFieldLabel, Phase Phase, int StartDay, int EndDay,
        // The manifest's competition title starts with this ("Xəzərin nəfəsi — poster müsabiqəsi").
        string ManifestTitlePrefix);

    public static readonly CompetitionDef[] Competitions =
    [
        new("xezerin-nefesi-poster", "xezer-sahili", "Xəzərin nəfəsi — poster müsabiqəsi",
            "Xəzərin şəhərlə əlaqəsini, sahil həyatını və dənizin ruhunu müasir poster dili ilə ifadə et.",
            "Poster Xəzər dənizi ilə Bakı arasındakı gündəlik əlaqəni — sahil gəzintilərini, balıqçıları, dəniz küləyini, "
            + "limanın səsini — bir baxışda oxunan vizual dillə çatdırmalıdır.",
            "Hər iştirakçı bir və ya bir neçə poster ideyası göndərə bilər. Poster şaquli formatda olmalı, üzərində mətn "
            + "varsa Azərbaycan dilində yazılmalıdır.",
            "Poster faylı (istəyə bağlı)", Phase.Open, -12, 24, "Xəzərin nəfəsi"),
        new("yasil-seher-vizual-kimlik", "yasil-baki", "Yaşıl şəhər — vizual kimlik müsabiqəsi",
            "Daha yaşıl, yaşana bilən və davamlı Bakı üçün vizual kimlik ideyası yarat.",
            "Müsabiqə şəhərin yaşıl təşəbbüsləri üçün vahid vizual kimlik — loqo, rəng palitrası və sadə qrafik sistem — "
            + "axtarır. İdeya parklarda, nəqliyyatda və rəqəmsal kanallarda eyni dərəcədə aydın oxunmalıdır.",
            "Təqdimatda loqo, rəng palitrası və ən azı iki tətbiq nümunəsi olmalıdır.",
            "Vizual material (istəyə bağlı)", Phase.Upcoming, 9, 50, "Yaşıl şəhər"),
        new("das-yaddasi-foto", "abseron-irsi", "Daş yaddaşı — foto müsabiqəsi",
            "Abşeronun daş memarlığını, qədim tikililərini və zamanın izlərini fotoqrafiya vasitəsilə sənədləşdir.",
            "Fotoqraflar Abşeronun qalalarını, məscidlərini, köhnə məhəllələrini və daşın üzərində zamanın buraxdığı izləri "
            + "çəkir. Müsabiqə həm sənədləşdirmə, həm də bədii baxış üçün açıq idi.",
            "Fotoşəkil sənədləşdirmə məqsədli olmalı, həddindən artıq rəqəmsal emal olunmamalıdır.",
            "Fotoşəkillər (istəyə bağlı)", Phase.Completed, -70, -9, "Daş yaddaşı"),
    ];

    /// <summary>One demo entry. Chevron entries take their image from the manifest by settlement; drafts have none.</summary>
    public sealed record EntryDef(
        string Person, string Title, string Concept, string? Symbols, string? Notes, SubmissionStatus Status,
        int SubmittedDaysAgo, string? ModerationComment = null, bool Winner = false);

    public sealed record SettlementDef(string Settlement, EntryDef[] Entries);

    /// <summary>
    /// The Bakı chevron competition: every settlement with a supplied chevron has exactly one public entry carrying that
    /// artwork (so the gallery never shows the same image twice); the other entries are drafts (private, no file) or
    /// rejected (hidden from the public, same settlement's artwork). Winners: 8 settlements; 4 more have entries but
    /// no winner yet.
    /// </summary>
    public static readonly SettlementDef[] Chevrons =
    [
        new("Balaxanı",
        [
            new("aysel", "Balaxanının yaddaşı",
                "Dizaynda Balaxanının neft sənayesi irsi ilə Abşeron memarlığının daş xarakteri birləşdirilib. Kompozisiyanın "
                + "mərkəzində sənaye yaddaşı dayanır, xarici elementlər isə qəsəbənin müasir inkişafını ifadə edir.",
                "Neft buruğu sənaye tarixini, daş memarlıq Abşeron kimliyini, bitki elementləri isə yenilənmə və davamlılığı simvolizə edir.",
                null, SubmissionStatus.Approved, 21, Winner: true),
            new("farid", "Neftin izi",
                "Şevronun əsasını qəsəbənin ilk neft quyularının siluetləri təşkil edir; tünd yaşıl fon torpağı, qızılı haşiyə "
                + "isə neftin bu torpağa gətirdiyi dəyişikliyi xatırladır.",
                "Quyu siluetləri tarixi mədənləri, qızılı haşiyə zəhməti və sərvəti ifadə edir.",
                null, SubmissionStatus.Rejected, 17,
                ModerationComment: "Eyni kompozisiya bu qəsəbə üçün artıq təqdim olunub; iş müsabiqə şərtlərinə uyğun deyil."),
            new("samir", "Sənaye yaddaşı",
                "Köhnə sənaye tikililərinin həndəsi xətləri əsasında sadə, oxunaqlı nişan hazırlanır.",
                null, null, SubmissionStatus.Draft, 6),
        ]),
        new("Ramana",
        [
            new("murad", "Ramana qalası",
                "Əsas element kimi Ramana qalasının silueti seçilib. Qalxan forması tarixi müdafiə xarakterini, həndəsi "
                + "elementlər isə müasir vizual dili ifadə edir.",
                "Qala qülləsi dözümlülüyü, qalxan forması qorunan kimliyi, alt hissədəki yaşıl budaqlar isə qəsəbənin bağlarını simvolizə edir.",
                "Şevronun sadələşdirilmiş, tək rəngli variantını da hazırlaya bilərəm.", SubmissionStatus.Approved, 20, Winner: true),
            new("zahra", "Qalanın kölgəsi",
                "Axşam işığında qalanın uzanan kölgəsi qəsəbənin tarixi ilə bu gününü birləşdirən xətt kimi təsvir olunur.",
                null, null, SubmissionStatus.Draft, 5),
            new("kamran", "Abşeron nəfəsi",
                "Külək və torpaq motivləri ilə Abşeronun iqlimini ifadə edən kompozisiya; mərkəzdə qala qülləsi dayanır.",
                "Dalğalı xətlər küləyi, qum rəngi torpağı, qüllə isə tarixi yaddaşı ifadə edir.",
                null, SubmissionStatus.Rejected, 15,
                ModerationComment: "Təqdim olunan fayl başqa iştirakçının işi ilə eynidir; iş qəbul edilmədi."),
        ]),
        new("Maştağa",
        [
            new("narmin", "Maştağa irsi",
                "Mərkəzdə qəsəbənin bağlarını təmsil edən qoca ağac dayanır; arxa plandakı tarixi tikililər Maştağanın mənəvi "
                + "irsini, yaşıl və qızılı rənglər isə bağçılıq ənənəsini göstərir.",
                "Ağac bərəkəti və nəsillər arasındakı bağı, tarixi tikililər irsi, qızılı haşiyə isə qəsəbənin dəyərini simvolizə edir.",
                null, SubmissionStatus.Approved, 19, Winner: true),
            new("rashad", "Maştağa bağları",
                "Bağ yollarının xəritəsi əsasında həndəsi naxış hazırlanır; rəng palitrası hələ seçilməyib.",
                null, null, SubmissionStatus.Draft, 3),
        ]),
        new("Mərdəkan",
        [
            new("tural", "Mərdəkan qalası",
                "Mərdəkan qalası əsas simvol kimi istifadə olunub. Xəzər dalğaları qəsəbənin sahil kimliyini, qızılı haşiyə isə "
                + "tarixi irsin dəyərini vurğulayır.",
                "Dördkünc qala tarixi və müdafiəni, dalğalar Xəzər sahilini, qızılı haşiyə isə irsin dəyərini ifadə edir.",
                null, SubmissionStatus.Approved, 18, Winner: true),
            new("aynur", "Xəzər və qala",
                "Qala və dəniz iki yarıya bölünmüş fonda qarşı-qarşıya qoyulub.",
                "Mavi dənizi, qəhvəyi daşı ifadə edir.",
                null, SubmissionStatus.Rejected, 14,
                ModerationComment: "Eskiz bu qəsəbə üçün təqdim olunmuş başqa işlə üst-üstə düşür; iş qəbul edilmədi."),
        ]),
        new("Şüvəlan",
        [
            new("leyla", "Şüvəlan sahili",
                "Şevronda Şüvəlanın sahili və mayak təsvir olunur. Uçan qağayılar dənizlə bağlı həyat tərzini, sakit dalğalar isə "
                + "qəsəbənin istirahət ənənəsini xatırladır.",
                "Mayak yol göstərən işığı, qağayılar azadlığı, dalğalar isə Xəzəri simvolizə edir.",
                null, SubmissionStatus.Approved, 16, Winner: true),
            new("gunel", "Dənizin nəfəsi",
                "Dəniz küləyinin formalaşdırdığı qum təpələri əsasında yumşaq xətli nişan eskizi.",
                null, null, SubmissionStatus.Draft, 4),
        ]),
        new("Buzovna",
        [
            new("orxan", "Buzovna dalğası",
                "Gün batımında dənizə çıxan yelkənli qayıq Buzovnanın dənizçilik ənənəsini, dinamik dalğalar isə sahilin "
                + "canlı ritmini ifadə edir.",
                "Yelkən səyahəti və cəsarəti, günəş istiliyi və qonaqpərvərliyi, dalğalar isə hərəkəti və həyatı simvolizə edir.",
                null, SubmissionStatus.Approved, 15, Winner: true),
            new("elvin", "Sahil küləyi",
                "Yelkən forması əsasında sadə nişan eskizi.",
                null, null, SubmissionStatus.Draft, 2),
        ]),
        new("Nardaran",
        [
            new("nigar", "Nardaran ruhu",
                "Mərkəzdə qəsəbənin tarixi qülləsi, önündə isə adı ilə səsləşən nar dayanır. Tünd qırmızı haşiyə narın rəngini, "
                + "yaşıl budaqlar isə bağları xatırladır.",
                "Nar qəsəbənin adını və bərəkətini, qüllə qədim yaşayış məskənini, budaqlar isə davamlılığı simvolizə edir.",
                null, SubmissionStatus.Approved, 13, Winner: true),
        ]),
        new("Biləcəri",
        [
            new("elvin", "Biləcərinin yaddaşı",
                "Mərkəzdə qəsəbənin tarixi tağlı tikilisi dayanır; ətrafdakı ağaclar və qızılı haşiyə Biləcərinin köklü "
                + "yaşayış tarixini göstərir.",
                "Tağlı tikili yaddaşı, ağaclar təbiəti, qızılı haşiyə isə tarixi dəyəri ifadə edir.",
                null, SubmissionStatus.Approved, 12, Winner: true),
        ]),
        new("Pirşağı",
        [
            new("samir", "Pirşağı sahili",
                "Pirşağının geniş çimərliyi və qayalıq sahili günəşin doğduğu anda təsvir olunub.",
                "Günəş yeni başlanğıcı, dalğalar dənizi, qayalar isə sahilin möhkəmliyini simvolizə edir.",
                null, SubmissionStatus.Submitted, 9),
        ]),
        new("Zirə",
        [
            new("zahra", "Zirənin nəfəsi",
                "Qamışlıqlar arasında ucalan mayak Zirənin dənizə açılan qapısını, mavi səma isə açıq üfüqü ifadə edir.",
                "Mayak yolu, qamışlar sahil təbiətini, mavi rəng isə Xəzəri simvolizə edir.",
                null, SubmissionStatus.UnderReview, 8),
        ]),
        new("Bilgəh",
        [
            new("kamran", "Bilgəh bağı",
                "Sahildə ucalan qoca ağac Bilgəhin bağlarını və dənizə yaxınlığını bir kompozisiyada birləşdirir.",
                "Ağac bağları, dalğalar dənizi, qızılı haşiyə isə qəsəbənin istirahət ənənəsini ifadə edir.",
                null, SubmissionStatus.Submitted, 7),
        ]),
        new("Əmircan",
        [
            new("aynur", "Əmircanın izi",
                "Əmircanın tarixi məscidi və minarələri çərçivəyə alınıb; qızılı xətlər qəsəbənin çoxəsrlik tarixinə işarədir.",
                "Minarələr mənəvi mərkəzi, sahil xətti birgə yaşayışı, qızılı rəng isə tarixi dəyəri ifadə edir.",
                null, SubmissionStatus.Submitted, 6),
        ]),
    ];

    /// <summary>Entries of the other demo competitions: text only (their file fields are optional; no artwork was supplied).</summary>
    public static readonly (string CompetitionSlug, EntryDef[] Entries)[] OtherEntries =
    [
        ("xezerin-nefesi-poster",
        [
            new("leyla", "Bulvar səhəri", "Səhər tezdən bulvarda idman edən insanlar və fonda sakit dəniz; poster minimal xətlərlə qurulub.", null, null, SubmissionStatus.Submitted, 9),
            new("orxan", "Liman küləyi", "Limandakı gəmi kranları və küləkdə dalğalanan bayraqlar dinamik diaqonal kompozisiya ilə verilir.", null, null, SubmissionStatus.Submitted, 8),
            new("zahra", "Balıqçı kəndi", "Sahil kəndində torlarını yığan balıqçılar; isti qum rəngləri ilə soyuq dəniz tonları qarşılaşdırılır.", null, null, SubmissionStatus.UnderReview, 7),
            new("murad", "Dalğanın səsi", "Dalğanın səs dalğasına çevrilməsi ideyası: dənizin səsi qrafik ritm kimi göstərilir.", null, null, SubmissionStatus.Submitted, 5),
            new("gunel", "Xəzər axşamı", "Gün batımında bulvarın işıqları dənizdə əks olunur; poster iki rəngli çap üçün nəzərdə tutulub.", null, null, SubmissionStatus.Submitted, 3),
            new("farid", "Sahil xəritəsi", "Abşeron sahil xəttinin sadələşdirilmiş xəritəsi və üzərində sevilən sahil məkanları.", null, null, SubmissionStatus.Draft, 1),
        ]),
        ("das-yaddasi-foto",
        [
            new("tural", "Qala divarında zaman", "Mərdəkan qalasının divarında əsrlərin buraxdığı çatlar yaxın plan çəkilib.", null, null, SubmissionStatus.Approved, 40),
            new("nigar", "Karvansaranın həyəti", "Səhər işığında boş karvansara həyəti; kölgələr tağların ritmini təkrarlayır.", null, null, SubmissionStatus.Approved, 38),
            new("rashad", "Köhnə məhəllə", "Daş küçədə oynayan uşaqlar və köhnə evlərin qapıları — gündəlik həyatın irslə görüşü.", null, null, SubmissionStatus.UnderReview, 33),
            new("aynur", "Tağlar", "Bir sıra tağın perspektivi ilə daş memarlığın həndəsəsi göstərilir.", null, null, SubmissionStatus.Submitted, 30),
            new("samir", "Günəş və daş", "Günortanın sərt işığında əhəngdaşı divarın faktura çəkilişi.", null, null, SubmissionStatus.Submitted, 26),
            new("kamran", "Unudulmuş qapı", "Tərk edilmiş evin oyma taxta qapısı; fotoşəkil həddindən artıq emal olunub.", null, null, SubmissionStatus.Rejected, 24,
                ModerationComment: "Fotoşəkil həddindən artıq rəqəmsal emal olunub; müsabiqə şərtlərinə uyğun deyil."),
        ]),
    ];

    public const string ResultAnnouncement =
        "Münsiflər heyəti hər qəsəbə üçün təqdim olunan işləri ayrıca qiymətləndirdi. Qaliblərə təbrik edirik; "
        + "qalib müəyyən edilməyən qəsəbələr üzrə qərar sonra açıqlanacaq.";
}
