using EventLoom.EntityFrameworkCore.PostgreSql;
using Npgsql;

namespace EventLoom.EntityFrameworkCore.UnitTests;

public sealed class PostgreSqlExceptionClassifierTests
{
    [Test]
    public async Task Classifies_postgresql_concurrency_and_unique_failures()
    {
        await Assert.That(Classify("40P01")).IsEqualTo(PostgreSqlExceptionClassification.Deadlock);
        await Assert.That(Classify("40001")).IsEqualTo(PostgreSqlExceptionClassification.SerializationFailure);
        await Assert.That(Classify("23505")).IsEqualTo(PostgreSqlExceptionClassification.UniqueViolation);
    }

    [Test]
    public async Task Classifies_provider_failures_as_transient()
    {
        await Assert.That(PostgreSqlExceptionClassifier.Classify(new NpgsqlException("connection failed")))
            .IsEqualTo(PostgreSqlExceptionClassification.Transient);
        await Assert.That(PostgreSqlExceptionClassifier.IsTransient(PostgreSqlExceptionClassification.Deadlock))
            .IsTrue();
        await Assert.That(PostgreSqlExceptionClassifier.IsTransient(PostgreSqlExceptionClassification.UniqueViolation))
            .IsFalse();
    }

    private static PostgreSqlExceptionClassification Classify(string sqlState) =>
        PostgreSqlExceptionClassifier.Classify(
            new PostgresException("failure", "ERROR", "ERROR", sqlState));
}
