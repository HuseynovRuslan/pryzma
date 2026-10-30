namespace Meydan.Infrastructure.Persistence.Seed;

/// <summary>
/// The 59 settlements (qəsəbələr) of Baku city, in the official order supplied by the project owner.
/// Source: State Statistics Committee of the Republic of Azerbaijan — administrative-territorial
/// classification of Baku city. Seeded as ordinary CompetitionCategory rows; there is no
/// "Settlement" entity in the model.
/// </summary>
public static class BakuSettlements
{
    public static readonly IReadOnlyList<string> Names =
    [
        "Biləcəri", "Binəqədi", "Xocəsən", "Sulutəpə", "28 May", "M.Ə. Rəsulzadə", "Əhmədli", "Binə", "Buzovna",
        "Qala", "Mərdəkan", "Şağan", "Şüvəlan", "Türkan", "Zirə", "Çeyildağ", "Ələt", "Baş Ələt", "Kotal",
        "Qarakosa", "Pirsaat", "Şıxlar", "Yeni Ələt", "Korgöz", "Qızıldaş", "Şonqar", "Qobustan", "Lökbatan",
        "Heybət", "Şubanı", "Müşviqabad", "Puta", "Sahil", "Qaradağ", "Sanqaçal", "Ümid", "Keşlə", "Çilov",
        "Gürgən", "Neft Daşları", "Pirallahı", "Bakıxanov", "Balaxanı", "Bilgəh", "Kürdəxanı", "Maştağa",
        "Nardaran", "Pirşağı", "Ramana", "Sabunçu", "Zabrat", "Badamdar", "Bibiheybət", "Bülbülə", "Əmircan",
        "Hövsan", "Qaraçuxur", "Yeni Suraxanı", "Zığ",
    ];
}
