namespace Warp.Latinum.Controllers;

public class StripeWebhookOptions
{
    public string CallbackUrl { get; set; } = "http://warp:5004/webhook/stripe/payment";
    public string Secret { get; set; } = "whsec_test_secret";
    public string[] Events { get; set; } = new[] { "payment_intent.succeeded", "payment_intent.payment_failed" };
    public string ConnectionString { get; set; } = "redis:6379";
    public string Channel { get; set; } = "stripe_payment_async";
    
    // Key resolution for quota updates - same as QuotaChecker
    public List<string> KeyHeaders { get; set; } = new() { "X-JWT-Email", "X-Api-Key" };
    
    // Additional properties for ngrok and Stripe API integration
    public string? NgrokAuthToken { get; set; }
    public string? StripeSecretKey { get; set; }
}