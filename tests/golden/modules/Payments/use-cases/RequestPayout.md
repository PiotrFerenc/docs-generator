---
title: "Payments / Zlecenie wypłaty dla klienta"
page_id: "T:Payments.Application.RequestPayoutHandler"
generated: true
source_commit: a1b2c3d
---

# Zlecenie wypłaty dla klienta

> Strona generowana automatycznie z kodu. Nie edytuj jej. Uzupełnienia dodawaj w `RequestPayout.notes.md`.

## Cel

Tworzy wypłatę dla klienta za rozliczone zlecenie skupu i przekazuje ją do realizacji przelewem.

## Kiedy się uruchamia

- Automatycznie po rozliczeniu zlecenia skupu, jeśli klient wybrał wypłatę przelewem ([Start wypłaty po rozliczeniu zlecenia](StartPayoutAfterSettlement.md)).
- Błąd zwrócony przez ten proces **nie jest pokazywany użytkownikowi ani ponawiany**. Jest tylko zapisywany w logach (szczegóły na stronie [Start wypłaty po rozliczeniu zlecenia](StartPayoutAfterSettlement.md)).

## Scenariusze

**Scenariusz główny:** wypłaty są włączone, dla zlecenia nie ma jeszcze wypłaty, klient ma aktywne konto ze zweryfikowaną tożsamością i podanym numerem konta, a kwota mieści się w limicie. System tworzy wypłatę ze statusem *Oczekująca* i przekazuje ją do wysłania przelewem.

**Scenariusze alternatywne:**

- **Kwota równa zero lub ujemna** (reguła 1): zlecenie wypłaty zostaje odrzucone z błędem.
- **Wypłaty wyłączone** (reguła 2): system nic nie robi i **nie zgłasza błędu**. Wypłata nie powstaje.
- **Wypłata dla tego zlecenia już istnieje** (reguła 3): system nic nie robi i nie zgłasza błędu. Wyjątek: jeśli poprzednia wypłata zakończyła się niepowodzeniem, powstaje nowa.
- **Konto klienta nie istnieje albo jest zablokowane** (reguła 4): błąd, wypłata nie powstaje.
- **Tożsamość klienta niezweryfikowana** (reguła 5): błąd, wypłata nie powstaje.
- **Brak numeru konta bankowego** (reguła 6): błąd, wypłata nie powstaje.
- **Kwota powyżej limitu** (reguła 7): błąd, wypłata nie powstaje.

## Reguły biznesowe

Kolejność odpowiada kolejności sprawdzania w kodzie.

| # | Reguła | Skutek niespełnienia |
|---|---|---|
| 1 | Kwota wypłaty musi być większa od zera. | Błąd walidacji. |
| 2 | Wypłaty muszą być włączone w konfiguracji systemu. | Brak akcji, bez błędu. |
| 3 | Dla zlecenia nie może istnieć wypłata oczekująca ani zrealizowana. Wypłata nieudana nie blokuje nowej. | Brak akcji, bez błędu. |
| 4 | Klient musi mieć konto, które nie jest zablokowane. | Błąd „konto nieaktywne”. |
| 5 | Klient musi mieć zweryfikowaną tożsamość (KYC). | Błąd „brak weryfikacji KYC”. |
| 6 | Klient musi mieć podany numer konta bankowego (IBAN). | Błąd „brak numeru konta”. |
| 7 | Kwota nie może przekraczać limitu jednej wypłaty (domyślnie 20 000, konfigurowalny). | Błąd „przekroczony limit”. |

## Efekty

- Tworzy [wypłatę](../entities/Payout.md) ze statusem *Oczekująca*.
- **Numer konta jest kopiowany do wypłaty w chwili jej utworzenia.** Późniejsza zmiana numeru konta klienta nie zmienia już utworzonej wypłaty.
- Uruchamia wysłanie przelewu do operatora płatności (poza zakresem przykładu).

## Obowiązujące reguły globalne

- [Walidacja komend](../../../global-rules/ValidationBehavior.md)

## Proces

- [Wypłata po rozliczeniu zlecenia skupu](../../../flows/Wyplata-po-rozliczeniu.md)

---

## Szczegóły techniczne

| | |
|---|---|
| Handler | `Payments.Application.RequestPayoutHandler` ([kod](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Application/RequestPayout.cs#L22)) |
| Komenda | `RequestPayoutCommand(OrderId, CustomerId, Amount)` → `Result` |
| Walidator | `RequestPayoutCommandValidator` ([kod](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Application/RequestPayout.cs#L12)) |
| Wywoływany przez | `OrderSettledEventHandler` (`ISender.Send`) |
| Publikuje | `PayoutRequestedEvent` (z `Payout.Create`, po commicie) |
| Zależności | `ICustomerAccountRepository`, `IPayoutRepository`, `IFeatureManager`, `IOptions<PayoutOptions>`, `TimeProvider` |

**Reguły w kodzie**

| # | Warunek | Wyjście | Miejsce |
|---|---|---|---|
| 1 | `RuleFor(x => x.Amount).GreaterThan(0)` | `Result.Fail` (ValidationBehavior) | [RequestPayout.cs:18](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Application/RequestPayout.cs#L18) |
| 2 | `!await features.IsEnabledAsync("Payouts.Enabled")` | `Result.Ok()` (cichy sukces) | [RequestPayout.cs:32](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Application/RequestPayout.cs#L32) |
| 3 | `payouts.ExistsForOrderAsync(OrderId)`, w repozytorium `p.OrderId == orderId && p.Status != PayoutStatus.Failed` | `Result.Ok()` (cichy sukces) | [RequestPayout.cs:35](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Application/RequestPayout.cs#L35), [PaymentsInfrastructure.cs:35](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Infrastructure/PaymentsInfrastructure.cs#L35) |
| 4 | `account is null`, w repozytorium `a.CustomerId == customerId && !a.IsBlocked` | `AccountNotActiveError` | [RequestPayout.cs:39](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Application/RequestPayout.cs#L39), [PaymentsInfrastructure.cs:27](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Infrastructure/PaymentsInfrastructure.cs#L27) |
| 5 | `account.KycStatus != KycStatus.Verified` | `KycNotVerifiedError` | [RequestPayout.cs:42](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Application/RequestPayout.cs#L42) |
| 6 | `string.IsNullOrWhiteSpace(account.Iban)` | `MissingIbanError` | [RequestPayout.cs:45](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Application/RequestPayout.cs#L45) |
| 7 | `amount > maxAmount` w `Payout.Create` | `PayoutLimitExceededError` | [Payments.cs:47](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Domain/Payments.cs#L47) |

Pominięte w części biznesowej (warunki techniczne): `RuleFor(x => x.OrderId).NotEmpty()`, `RuleFor(x => x.CustomerId).NotEmpty()` ([RequestPayout.cs:16–17](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Application/RequestPayout.cs#L16)).

**Dane**

| Operacja | Tabela | Kolumny |
|---|---|---|
| odczyt | `customer_accounts` | `customer_id`, `is_blocked`, `kyc_status`, `iban` |
| odczyt | `payouts` | `order_id`, `status` |
| zapis (insert) | `payouts` | `id`, `order_id`, `customer_id`, `amount`, `iban`, `status`, `created_at` |

**Konfiguracja**

| Klucz | Znaczenie | Domyślnie |
|---|---|---|
| `Payouts.Enabled` | feature flag włączający wypłaty | brak (zależy od konfiguracji środowiska) |
| `Payouts:MaxAmount` | limit jednej wypłaty (`PayoutOptions.MaxAmount`) | `20000` |

## Uwagi zespołu

(treść dołączana z `RequestPayout.notes.md`, jeśli istnieje)
