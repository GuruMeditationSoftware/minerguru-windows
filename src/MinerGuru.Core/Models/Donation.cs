namespace MinerGuru.Core.Models;

/// <summary>Wallet addresses for voluntary donations (also shown on the website).</summary>
public sealed record DonationCoin(string Symbol, string Name, string Address)
{
    public static readonly DonationCoin Bitcoin = new("BTC", "Bitcoin", "bc1qc6xpuwkys8jsjt439tfw2eu3fctkx7ad0hkehe");
    public static readonly DonationCoin BitcoinCash = new("BCH", "Bitcoin Cash", "bitcoincash:qruvqq2x9vyd9gykre058r4lda4gulpres33nr9aml");
    public static readonly IReadOnlyList<DonationCoin> All = [Bitcoin, BitcoinCash];

    /// <summary><c>false</c> as long as the build carries a placeholder instead of a real address.</summary>
    public bool IsConfigured => !Address.Contains("ADRESSE-EINTRAGEN");

    /// <summary>BIP21-style payment URI, so wallet apps open it directly when the QR code is scanned.</summary>
    public string PaymentUri => Symbol == "BTC" ? $"bitcoin:{Address}" : Address;
}
