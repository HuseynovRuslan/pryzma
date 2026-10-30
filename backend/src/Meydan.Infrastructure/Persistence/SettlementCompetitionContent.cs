using System.Text;
using System.Text.Json;

namespace Meydan.Infrastructure.Persistence;

/// <summary>
/// The published words of the two settlement competitions, in one place because both say the same things and
/// they must not drift apart before an admin edits them.
///
/// The file rules are the one part that is not written by hand: they are read back out of the competition's own
/// File field, so the sentence a participant reads cannot promise something the validator would refuse. If the
/// limits are ever changed in the admin, the sentence is what has to be re-read — never the other way round.
/// </summary>
internal static class SettlementCompetitionContent
{
    /// <summary>What the first version of the launch wrote; anything still equal to it is ours to update.</summary>
    public const string OriginalRequirements =
        "Müsabiqəyə rəssamlar, qrafik dizaynerlər, memarlar, illüstratorlar və yaradıcı gənclər müraciət edə bilərlər.";

    public const string EligibilityLine = OriginalRequirements;

    /// <summary>
    /// The nominations as rows rather than prose: the exact text of the entry form's Select option, and the name
    /// the nomination is known by in an address. The prose in <see cref="Nominations"/> stays what an admin owns
    /// and rewrites; this is the pair a vote can be counted against, so it is fixed.
    ///
    /// <para>
    /// The option text is matched exactly, never approximately. A submission whose answer is not one of these is
    /// left unlinked and logged rather than guessed at — see docs/voting/OWNER-DECISIONS.md, D1.
    /// </para>
    /// </summary>
    public static readonly (string Option, string Slug)[] NominationSlugs =
    [
        ("Simvol və Loqotip Dizaynı", "simvol-loqotip"),
        ("Promo Məhsul Tətbiqləri", "promo-mehsul"),
        ("Məkan və Küçə Tərtibatı", "mekan-kuce"),
    ];

    /// <summary>
    /// Work made with a machine is welcome, and the participation section is where someone goes to find that out.
    /// Kept as its own constant so the block can be recognised inside text that already exists: taking it back out
    /// of what <see cref="Requirements"/> produces today gives, character for character, what it produced before.
    /// </summary>
    public const string AiNote =
        "Süni intellekt\n"
        + "Süni intellekt vasitəsilə hazırlanmış təqdimatlar da qəbul edilir. Əsas olan yanaşmanın "
        + "özünəməxsusluğu və ideyanın orijinallığıdır.\n\n";

    /// <summary>
    /// The line under the poster, and the lede on the competition page. It is a label rather than a sentence, so it
    /// ends without a full stop — see the note on short public copy in the frontend.
    /// </summary>
    public const string NovxaniShortDescription =
        "Novxanının tarixi, mədəni irsi və özünəməxsus kimliyini müasir dizayn dili ilə ifadə edən simvol "
        + "və yaradıcı vizual konseptlərin hazırlanması üçün açıq müsabiqə";

    /// <inheritdoc cref="NovxaniShortDescription"/>
    public const string EmircanShortDescription =
        "Əmircanın zəngin tarixi, memarlığı və yaradıcı irsini müasir vizual dillə ifadə edən simvol və "
        + "dizayn konseptlərinin hazırlanması üçün açıq müsabiqə";

    public const string Prize =
        "1-ci yer — 250 AZN + Sertifikat\n2-ci yer — 150 AZN + Sertifikat\n3-cü yer — 100 AZN + Sertifikat\n\n"
        + "Tətbiq imkanı\nSeçilmiş simvol və dizaynlar abadlıq layihələrində, şəhər və küçə tərtibatında, eləcə də "
        + "rəsmi promo məhsulların hazırlanmasında istifadə oluna bilər.";

    public const string Nominations =
        "Simvol və Loqotip Dizaynı\n"
        + "Qəsəbənin tarixini, mədəniyyətini və yerli kimliyini müasir vizual dildə ifadə edən simvol və loqotip "
        + "konseptləri.\n\n"
        + "Promo Məhsul Tətbiqləri\n"
        + "Hazırlanmış vizual kimliyin eko-çanta, fincan, geyim, qələm, brelok və digər promo/suvenir "
        + "məhsullarında tətbiqini göstərən konseptlər.\n\n"
        + "Məkan və Küçə Tərtibatı\n"
        + "Küçə, bina divarı və ictimai məkanlarda istifadə oluna biləcək mural, mozaika, əhəngdaşı relyefi, "
        + "informasiya lövhəsi və açıq hava installyasiyası konseptləri.";

    public const string Rules =
        "Təqdim olunan iş iştirakçının öz yaradıcı işi olmalıdır.\n"
        + "Üçüncü şəxslərin müəlliflik və digər hüquqları pozulmamalıdır; başqa müəllifin işindən icazəsiz "
        + "istifadəyə yol verilmir.\n"
        + "İştirakçı təqdim etdiyi məlumatların düzgünlüyünə görə məsuliyyət daşıyır.\n"
        + "Eyni iş yanlış və ya yanıltıcı məlumatla təqdim edilməməlidir.\n"
        + "Təşkilatçı qaydalara uyğun olmayan təqdimatı qiymətləndirmədən kənarlaşdıra bilər.\n"
        + "Seçilmiş işin tətbiqi texniki uyğunlaşdırma tələb edə bilər.";

    public const string JudgingCriteria =
        "Təqdimatlar aşağıdakı meyarlar üzrə dəyərləndirilir:\n\n"
        + "Yerli kimliyə və müsabiqə mövzusuna uyğunluq\n"
        + "Orijinallıq və yaradıcı yanaşma\n"
        + "Vizual keyfiyyət və ifadəlilik\n"
        + "Praktik tətbiq imkanları\n"
        + "Müxtəlif ölçü və daşıyıcılarda istifadə oluna bilmə\n\n"
        + "Praktik tətbiq seçilmiş nominasiyaya görə dəyərləndirilir: promo məhsul konsepti məhsulun üzərində, "
        + "məkan və küçə tərtibatı isə real mühitdə nəzərdən keçirilir.";

    public const string UsageTerms =
        "İştirakçı təqdim etdiyi işin müəllifi olduğunu və üçüncü şəxslərin hüquqlarını pozmadığını təsdiq edir.\n\n"
        + "Müsabiqəyə təqdim olunan iş təşkilatçı tərəfindən müsabiqənin tanıdılması, sərgi, nəticələrin elanı və "
        + "kommunikasiya materiallarında müəllif göstərilməklə nümayiş etdirilə bilər.\n\n"
        + "Qalib və ya tətbiq üçün seçilmiş dizaynın istehsal, kommersiya və ya geniş miqyaslı tətbiqi üçün zəruri "
        + "istifadə hüquqları tərəflər arasında ayrıca razılaşdırıla bilər.";

    public const string ConceptHelp =
        "İdeyanın nədən yarandığını, nəyi ifadə etdiyini və qəsəbə ilə bağlılığını qısa izah edin.";

    /// <param name="settlement">"Novxanı" / "Əmircan" — the entry form asks about that settlement, not about both.</param>
    public static string SymbolsHelp(string settlement) =>
        $"Hansı elementlərdən istifadə etdiyinizi (memarlıq detalı, ornament, təbiət, rəng) və onların {settlement} " +
        "ilə əlaqəsini yazın.";

    /// <summary>The whole participation section: who may enter, what a submission consists of, and the file rules.</summary>
    public static string Requirements(string? fileFieldOptionsJson) =>
        EligibilityLine + "\n\n"
        + AiNote
        + "Təqdimat üçün nə lazımdır\n"
        + "Nominasiyanı seçin, işin adını yazın, konsepti və istifadə etdiyiniz simvolların mənasını izah edin, "
        + "sonra iş fayllarını yükləyin. “Əlavə qeyd” sahəsi istəyə bağlıdır.\n\n"
        + "Fayllar\n"
        + FileRules(fileFieldOptionsJson) + " Eskiz, maket və izahlı səhifələr bir təqdimatın içində birlikdə "
        + "göndərilə bilər.\n\n"
        + "Təqdimatların sayı\n"
        + "Bir iştirakçı bir neçə nominasiyaya, eləcə də hər iki qəsəbə müsabiqəsinə ayrıca təklif göndərə bilər.";

    /// <summary>One sentence, built from the field's own limits — never from a number typed here.</summary>
    public static string FileRules(string? optionsJson)
    {
        var (maxFiles, maxMb, formats) = ReadFileLimits(optionsJson);
        var text = new StringBuilder();
        if (formats.Count > 0)
            text.Append("Qəbul olunan formatlar: ").Append(JoinAz(formats)).Append(". ");
        if (maxFiles is not null)
            text.Append("Ən çox ").Append(maxFiles).Append(" fayl");
        if (maxFiles is not null && maxMb is not null)
            text.Append(", hər biri ən çox ").Append(maxMb).Append(" MB");
        else if (maxMb is not null)
            text.Append("Hər fayl ən çox ").Append(maxMb).Append(" MB");
        if (maxFiles is not null || maxMb is not null)
            text.Append('.');
        return text.ToString().TrimEnd();
    }

    private static (int? MaxFiles, int? MaxMb, IReadOnlyList<string> Formats) ReadFileLimits(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson))
            return (null, null, []);
        try
        {
            using var document = JsonDocument.Parse(optionsJson);
            var root = document.RootElement;
            int? Number(string name) =>
                root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
            var formats = root.TryGetProperty("accept", out var accept) && accept.ValueKind == JsonValueKind.Array
                ? accept.EnumerateArray().Select(x => x.GetString()).OfType<string>()
                    .Select(x => x.ToUpperInvariant()).ToList()
                : [];
            return (Number("maxFiles"), Number("maxFileSizeMb"), formats);
        }
        catch (JsonException)
        {
            return (null, null, []);
        }
    }

    /// <summary>"PNG, JPEG, WEBP və PDF" — the last item joined with "və", the way a sentence reads.</summary>
    private static string JoinAz(IReadOnlyList<string> items) =>
        items.Count switch
        {
            0 => "",
            1 => items[0],
            _ => string.Join(", ", items.Take(items.Count - 1)) + " və " + items[^1],
        };
}
