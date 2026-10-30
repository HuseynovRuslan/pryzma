namespace Meydan.Domain.Common;

/// <summary>Entities whose CreatedAt/UpdatedAt are stamped by the DbContext.</summary>
public interface IAuditable
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
}
