namespace ECHAT.Server.Core.Exceptions;

/// <summary>Risorsa non trovata. I controller mappano in 404.</summary>
public class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message) { }
}

/// <summary>Operazione vietata per ruolo / ownership. I controller mappano in 403.</summary>
public class ForbiddenException : Exception
{
    public ForbiddenException(string message = "Forbidden") : base(message) { }
}

/// <summary>
/// Stato in conflitto (es. utente già membro). I controller mappano in 409 Conflict.
/// Da NON usare per errori di validazione argomenti: per quelli c'è <see cref="ValidationException"/>.
/// </summary>
public class ConflictException : Exception
{
    public ConflictException(string message) : base(message) { }
}

/// <summary>
/// Argomento non valido fornito dal chiamante (es. ruolo sconosciuto, nome vuoto). I controller
/// mappano in 400 Bad Request. Distinta da <see cref="ConflictException"/> (409), che indica un
/// conflitto di stato e non un input malformato.
/// </summary>
public class ValidationException : Exception
{
    public ValidationException(string message) : base(message) { }
}

/// <summary>
/// Un'altra transazione ha modificato la stessa riga tra la nostra Read e Write. Sollevata
/// dagli store quando un [ConcurrencyCheck] EF fallisce. Le ops idempotenti (Cancel, Finalize,
/// Checkpoint sul job) la trattano come "qualcun altro ha vinto la corsa, return".
/// </summary>
public class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(string message = "Concurrency conflict") : base(message) { }
}
