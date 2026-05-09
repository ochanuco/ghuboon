using System.Runtime.CompilerServices;

// Phase 6: expose internal DTOs and the mapper to the test assembly so unit tests can
// exercise wire-format -> domain mapping without leaking DTOs to consumers (ADR-009).
[assembly: InternalsVisibleTo("Ghuboon.Tests")]
