---
title: "Reguły globalne / Walidacja komend"
page_id: "T:BuildingBlocks.ValidationBehavior`2"
generated: true
source_commit: a1b2c3d
---

# Walidacja komend

> Strona generowana automatycznie z kodu. Nie edytuj jej. Uzupełnienia dodawaj w `ValidationBehavior.notes.md`.

## Cel

Sprawdza poprawność danych każdej komendy, zanim zostanie wykonana jej logika.

## Działanie

- Dotyczy każdej komendy, która ma zdefiniowane reguły walidacji.
- Sprawdzane są **wszystkie** reguły naraz, a nie tylko pierwsza niespełniona. Błąd zawiera listę wszystkich problemów z nazwami pól.
- Jeśli którakolwiek reguła nie jest spełniona, **logika komendy w ogóle się nie wykonuje** i nic nie jest zapisywane.
- Błąd walidacji jest zwracany jako wynik, a nie wyjątek. To wywołujący decyduje, co z nim zrobić (np. endpoint zwraca HTTP 422, a [Start wypłaty po rozliczeniu zlecenia](../modules/Payments/use-cases/StartPayoutAfterSettlement.md) tylko zapisuje ostrzeżenie w logach).

## Gdzie obowiązuje

| Proces | Reguły walidacji |
|---|---|
| [Rozliczenie zlecenia skupu](../modules/Orders/use-cases/SettleOrder.md) | tylko techniczne |
| [Zlecenie wypłaty dla klienta](../modules/Payments/use-cases/RequestPayout.md) | reguła 1 |
| [Potwierdzenie wypłaty przez operatora płatności](../modules/Payments/use-cases/ConfirmPayout.md) | reguły 1–2 |

---

## Szczegóły techniczne

| | |
|---|---|
| Klasa | `BuildingBlocks.ValidationBehavior<TRequest, TResponse>` ([kod](https://git.example/skup/-/blob/a1b2c3d/src/BuildingBlocks/BuildingBlocks.cs#L25)) |
| Typ | `IPipelineBehavior<TRequest, TResponse>`, `TResponse : ResultBase, new()` |
| Źródło reguł | wszystkie `IValidator<TRequest>` (FluentValidation) z kontenera DI |

**Logika w kodzie**

| Warunek | Wyjście | Miejsce |
|---|---|---|
| `!validators.Any()` | `next()`, bez walidacji | [BuildingBlocks.cs:32](https://git.example/skup/-/blob/a1b2c3d/src/BuildingBlocks/BuildingBlocks.cs#L32) |
| `failures.Count == 0` | `next()` | [BuildingBlocks.cs:38](https://git.example/skup/-/blob/a1b2c3d/src/BuildingBlocks/BuildingBlocks.cs#L38) |
| w przeciwnym razie | nowy `TResponse` z `Error(ErrorMessage)` i metadaną `Property` dla każdego błędu | [BuildingBlocks.cs:41](https://git.example/skup/-/blob/a1b2c3d/src/BuildingBlocks/BuildingBlocks.cs#L41) |

## Uwagi zespołu

(treść dołączana z `ValidationBehavior.notes.md`, jeśli istnieje)
