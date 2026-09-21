using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EventLoom.EntityFrameworkCore.PostgreSql;

/// <summary>Classifies PostgreSQL failures relevant to EventLoom retry and conflict handling.</summary>
internal enum PostgreSqlExceptionClassification
{
    /// <summary>The exception is not recognized as a PostgreSQL provider failure.</summary>
    Unknown,

    /// <summary>The provider failure is transient and may succeed when retried.</summary>
    Transient,

    /// <summary>A concurrent transaction deadlock was detected.</summary>
    Deadlock,

    /// <summary>A concurrent transaction serialization failure occurred.</summary>
    SerializationFailure,

    /// <summary>A unique constraint conflict occurred.</summary>
    UniqueViolation
}

/// <summary>Provides stable classification for PostgreSQL exceptions.</summary>
internal static class PostgreSqlExceptionClassifier
{
    /// <summary>
    /// Classifies an exception, including EF Core update exceptions that wrap a provider exception.
    /// </summary>
    /// <param name="exception">The exception to classify.</param>
    /// <returns>The PostgreSQL classification.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    public static PostgreSqlExceptionClassification Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var providerException = exception;
        while (providerException is DbUpdateException && providerException.InnerException is not null)
        {
            providerException = providerException.InnerException;
        }

        if (providerException is PostgresException postgresException)
        {
            return postgresException.SqlState switch
            {
                PostgresErrorCodes.DeadlockDetected => PostgreSqlExceptionClassification.Deadlock,
                PostgresErrorCodes.SerializationFailure => PostgreSqlExceptionClassification.SerializationFailure,
                PostgresErrorCodes.UniqueViolation => PostgreSqlExceptionClassification.UniqueViolation,
                "08000" or "08001" or "08003" or "08006" or "53300" or "57P01" or "57P02" or "57P03"
                    => PostgreSqlExceptionClassification.Transient,
                _ => PostgreSqlExceptionClassification.Unknown
            };
        }

        return providerException is NpgsqlException
            ? PostgreSqlExceptionClassification.Transient
            : PostgreSqlExceptionClassification.Unknown;
    }

    /// <summary>Returns whether a classification represents a retryable PostgreSQL failure.</summary>
    /// <param name="classification">The classification to inspect.</param>
    public static bool IsTransient(PostgreSqlExceptionClassification classification) =>
        classification is PostgreSqlExceptionClassification.Transient
            or PostgreSqlExceptionClassification.Deadlock
            or PostgreSqlExceptionClassification.SerializationFailure;
}
