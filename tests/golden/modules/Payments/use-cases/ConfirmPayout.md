---
title: "Payments / Potwierdzenie wypłaty przez operatora płatności"
page_id: "T:Payments.Application.ConfirmPayoutHandler"
generated: true
source_commit: a1b2c3d
---

# Potwierdzenie wypłaty przez operatora płatności

> Strona generowana automatycznie z kodu. Nie edytuj jej. Uzupełnienia dodawaj w `ConfirmPayout.notes.md`.

## Cel

Zapisuje wynik przelewu zgłoszony przez operatora płatności: wypłata zrealizowana albo nieudana.

## Kiedy się uruchamia

- Automatycznie, gdy operator płatności wyśle powiadomienie o wykonaniu lub odrzuceniu przelewu.
- Za udany przelew system uznaje **tylko** powiadomienie ze statusem `executed`. Każdy inny status operatora jest traktowany jako niepowodzenie.

## Scenariusze

**Scenariusz główny (sukces):** wypłata istnieje i czeka na realizację, a operator potwierdza wykonanie przelewu z numerem referencyjnym. System oznacza wypłatę jako *Zrealizowana* i zapisuje numer referencyjny.

**Scenariusz główny (odrzucenie):** wypłata istnieje i czeka na realizację, a operator zgłasza odrzucenie z podanym powodem. System oznacza wypłatę jako *Nieudana* i zapisuje powód.

**Scenariusze alternatywne:**

- **Brak numeru referencyjnego przy sukcesie albo brak powodu przy odrzuceniu** (reguły 1–2): powiadomienie odrzucone, stan wypłaty bez zmian.
- **Wypłata nie istnieje** (reguła 3): powiadomienie odrzucone.
- **Wypłata jest już zrealizowana albo nieudana** (reguła 4), np. operator wysłał powiadomienie drugi raz: powiadomienie odrzucone, stan wypłaty bez zmian.

**Po nieudanej wypłacie** w analizowanym kodzie nic nie tworzy automatycznie nowej wypłaty. Nowa wypłata dla tego samego zlecenia jest dozwolona ([Zlecenie wypłaty dla klienta](RequestPayout.md), reguła 3), ale musi zostać zlecona w inny sposób.

## Reguły biznesowe

| # | Reguła | Skutek niespełnienia |
|---|---|---|
| 1 | Potwierdzenie sukcesu musi zawierać numer referencyjny przelewu. | Błąd walidacji. |
| 2 | Zgłoszenie odrzucenia musi zawierać powód. | Błąd walidacji. |
| 3 | Wypłata musi istnieć. | Błąd „nie znaleziono wypłaty”. |
| 4 | Wypłata musi mieć status *Oczekująca*. | Błąd „wypłata już zakończona”. |

## Efekty

- Sukces: [wypłata](../entities/Payout.md) przechodzi w status *Zrealizowana*, zapisany zostaje numer referencyjny.
- Odrzucenie: wypłata przechodzi w status *Nieudana*, zapisany zostaje powód.

## Obowiązujące reguły globalne

- [Walidacja komend](../../../global-rules/ValidationBehavior.md)

## Proces

- [Wypłata po rozliczeniu zlecenia skupu](../../../flows/Wyplata-po-rozliczeniu.md)

---

## Szczegóły techniczne

| | |
|---|---|
| Handler | `Payments.Application.ConfirmPayoutHandler` ([kod](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Application/ConfirmPayout.cs#L20)) |
| Komenda | `ConfirmPayoutCommand(PayoutId, Succeeded, PspReference, FailureReason)` → `Result` |
| Walidator | `ConfirmPayoutCommandValidator` ([kod](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Application/ConfirmPayout.cs#L10)) |
| Wyzwalacz | `POST /webhooks/psp/payouts` ([kod](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Api/PspWebhookEndpoints.cs#L14)); `Succeeded = body.Status == "executed"` ([kod](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Api/PspWebhookEndpoints.cs#L18)); błąd → HTTP 422 |
| Zależności | `IPayoutRepository` |

**Reguły w kodzie**

| # | Warunek | Wyjście | Miejsce |
|---|---|---|---|
| 1 | `RuleFor(x => x.PspReference).NotEmpty().When(x => x.Succeeded)` | `Result.Fail` (ValidationBehavior) | [ConfirmPayout.cs:15](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Application/ConfirmPayout.cs#L15) |
| 2 | `RuleFor(x => x.FailureReason).NotEmpty().When(x => !x.Succeeded)` | `Result.Fail` (ValidationBehavior) | [ConfirmPayout.cs:16](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Application/ConfirmPayout.cs#L16) |
| 3 | `payout is null` | `PayoutNotFoundError` | [ConfirmPayout.cs:26](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Application/ConfirmPayout.cs#L26) |
| 4 | `Status != PayoutStatus.Pending` w `MarkCompleted` / `MarkFailed` | `PayoutAlreadyFinalizedError` | [Payments.cs:66](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Domain/Payments.cs#L66), [Payments.cs:76](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Domain/Payments.cs#L76) |

Pominięte w części biznesowej (warunki techniczne): `RuleFor(x => x.PayoutId).NotEmpty()` ([ConfirmPayout.cs:14](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Application/ConfirmPayout.cs#L14)).

**Dane**

| Operacja | Tabela | Kolumny |
|---|---|---|
| odczyt | `payouts` | `id`, `status` |
| zapis (update) | `payouts` | `status` → `Completed` + `psp_reference` albo `status` → `Failed` + `failure_reason` |

## Uwagi zespołu

(treść dołączana z `ConfirmPayout.notes.md`, jeśli istnieje)
