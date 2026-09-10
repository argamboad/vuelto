using System.Text.Json.Serialization;
using Vuelto.Api.Services;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Features.Cards;

// CARDS-1 DTOs (ADR-V021). Wire format: snake_case.

public record CardResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("brand")] string Brand,
    [property: JsonPropertyName("last4")] string Last4,
    [property: JsonPropertyName("bank_id")] Guid? BankId,
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("auto_named")] bool AutoNamed,
    [property: JsonPropertyName("identities")] IReadOnlyList<CardIdentityResponse> Identities,
    [property: JsonPropertyName("kind")] string Kind = CardKinds.Credit,
    [property: JsonPropertyName("backfilled")] int? Backfilled = null)
{
    /// <param name="identities">Every (brand, last four) the card is known by, oldest first — more than one after a renewal was merged.</param>
    /// <param name="backfilled">Only on an update that asked for it (CARDS-3): how many past transactions had their payment method corrected.</param>
    public static CardResponse From(Card c, IReadOnlyList<CardIdentityResponse>? identities, int? backfilled = null) =>
        new(c.Id, c.Name, c.Brand, c.Last4, c.BankId, c.IsActive, c.AutoNamed, identities ?? [new(c.Brand, c.Last4)], c.Kind, backfilled);
}

public record CardIdentityResponse([property: JsonPropertyName("brand")] string Brand, [property: JsonPropertyName("last4")] string Last4);

/// <summary>"This is the same card, renewed": everything of the card in the route moves under <c>into</c>.</summary>
public record MergeCardRequest([property: JsonPropertyName("into")] Guid? Into);

/// <summary><c>last4</c> may be the four digits or the masked number a voucher prints; <c>brand</c> defaults to <c>CARD</c>.</summary>
public record CreateCardRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("brand")] string? Brand,
    [property: JsonPropertyName("last4")] string? Last4,
    [property: JsonPropertyName("bank_id")] Guid? BankId,
    [property: JsonPropertyName("kind")] string? Kind = null);

/// <summary>Brand and last four are what the voucher prints — they identify the card and are not edited; the alias, the bank and the state are.</summary>
public record UpdateCardRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("bank_id")] Guid? BankId,
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("kind")] string? Kind = null,
    /// <summary>Also correct the payment method of every transaction already booked on this card (CARDS-3) — off by default: changing the card never rewrites history behind your back.</summary>
    [property: JsonPropertyName("backfill_payment_method")] bool BackfillPaymentMethod = false);

/// <summary>The 409 body — the catalog shape: the existing card's id and alias so the client can offer reactivation or point at the duplicate.</summary>
public record CardConflictResponse(
    string Error,
    string Message,
    [property: JsonPropertyName("existing_id")] Guid? ExistingId,
    [property: JsonPropertyName("existing_name")] string? ExistingName) : ErrorResponse(Error, Message);
