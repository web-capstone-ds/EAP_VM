namespace DsEap.Events.Models;

// API 명세서 §7 RECIPE_CHANGED — QoS 2, Retain=true
public sealed class RecipeChangedPayload
{
    public string MessageId { get; set; } = Guid.NewGuid().ToString();
    public string EventType { get; set; } = EventTypes.RecipeChanged;
    public string Timestamp { get; set; } = "";
    public string EquipmentId { get; set; } = "";
    public string EquipmentStatus { get; set; } = EquipmentStatuses.Idle;
    public string PreviousRecipeId { get; set; } = "";
    public string PreviousRecipeVersion { get; set; } = "";
    public string NewRecipeId { get; set; } = "";
    public string NewRecipeVersion { get; set; } = "";
    public string ChangedBy { get; set; } = "";
}
