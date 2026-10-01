using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FateAndFridge.Api.Services;

/// <summary>Shared PostgreSQL error detection for mapped write outcomes.</summary>
internal static class PostgresErrors
{
    /// <summary>True when the failure is a unique-constraint violation (SQLSTATE 23505).</summary>
    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        };

    /// <summary>True when the failure is a foreign-key violation (SQLSTATE 23503).</summary>
    public static bool IsForeignKeyViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.ForeignKeyViolation,
        };
}
