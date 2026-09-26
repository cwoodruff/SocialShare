using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace SocialShare.Core.Data;

/// <summary>
/// SQLite stores a DateTimeOffset as text that carries its offset, and the provider refuses to
/// translate an ordering comparison on it. Everything here is UTC anyway, so every
/// DateTimeOffset is stored as a plain UTC DateTime, which SQLite orders correctly and which
/// lets the scheduler compare "is this due yet" in SQL rather than in memory.
/// </summary>
public sealed class UtcDateTimeOffsetConverter()
    : ValueConverter<DateTimeOffset, DateTime>(
        offset => offset.UtcDateTime,
        value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero));
