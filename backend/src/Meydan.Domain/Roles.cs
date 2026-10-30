namespace Meydan.Domain;

public static class Roles
{
    public const string Participant = "Participant";
    public const string Admin = "Admin";
    public const string Jury = "Jury";

    public static readonly string[] All = [Participant, Admin, Jury];
}
