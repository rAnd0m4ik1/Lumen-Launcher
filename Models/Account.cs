namespace CraftDeck.Models;

public class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = "Offline";
    public string Username { get; set; } = "Player";
    public string? UUID { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }

    public bool IsMicrosoft => Type == "Microsoft";
}
