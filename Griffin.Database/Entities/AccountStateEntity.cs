using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Griffin.Database.Entities;

[Table("AccountStates")]
public class AccountStateEntity
{
    [Key]
    public uint Id { get; set; }

    public uint AccountUid { get; set; }

    [Required, MaxLength(128)]
    public string Key { get; set; } = string.Empty;

    /// <summary>JSON payload for per-account state such as pity, purchases, mail and claimed rewards.</summary>
    [Required, Column(TypeName = "jsonb")]
    public string Json { get; set; } = "{}";

    public DateTime UpdatedAt { get; set; }

    [ForeignKey(nameof(AccountUid))]
    public virtual AccountEntity Account { get; set; } = null!;
}
