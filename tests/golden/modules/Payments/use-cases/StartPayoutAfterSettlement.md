---
title: "Payments / Start wypłaty po rozliczeniu zlecenia"
page_id: "T:Payments.Application.OrderSettledEventHandler"
generated: true
source_commit: a1b2c3d
---

# Start wypłaty po rozliczeniu zlecenia

> Strona generowana automatycznie z kodu. Nie edytuj jej. Uzupełnienia dodawaj w `StartPayoutAfterSettlement.notes.md`.

## Cel

Po rozliczeniu zlecenia skupu uruchamia wypłatę przelewem, jeśli klient wybrał taką formę wypłaty.

## Kiedy się uruchamia

- Automatycznie, po zapisaniu rozliczenia zlecenia ([Rozliczenie zlecenia skupu](../../Orders/use-cases/SettleOrder.md)). Działa w tle, już po tym, jak specjalista dostał potwierdzenie rozliczenia.

## Scenariusze

**Scenariusz główny:** klient wybrał wypłatę przelewem. System zleca wypłatę ([Zlecenie wypłaty dla klienta](RequestPayout.md)).

**Scenariusze alternatywne:**

- **Klient wybrał wypłatę bonem** (reguła 1): ten proces nic nie robi. Bony obsługuje moduł bonów (poza zakresem przykładu).
- **Zlecenie wypłaty zakończyło się błędem**: błąd jest **tylko zapisywany w logach jako ostrzeżenie**. Nikt nie dostaje powiadomienia, proces nie jest ponawiany, a zlecenie skupu pozostaje rozliczone bez wypłaty.

## Reguły biznesowe

| # | Reguła | Skutek niespełnienia |
|---|---|---|
| 1 | Wypłata przelewem tylko wtedy, gdy klient wybrał przelew jako formę wypłaty. | Brak akcji, bez błędu. |

## Efekty

- Uruchamia [Zlecenie wypłaty dla klienta](RequestPayout.md) z kwotą równą zaakceptowanej cenie zlecenia.
- Przy błędzie zlecenia wypłaty zapisuje ostrzeżenie w logach.

## Proces

- [Wypłata po rozliczeniu zlecenia skupu](../../../flows/Wyplata-po-rozliczeniu.md)

---

## Szczegóły techniczne

| | |
|---|---|
| Handler | `Payments.Application.OrderSettledEventHandler` ([kod](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Application/OrderSettledEventHandler.cs#L7)) |
| Typ | `INotificationHandler<OrderSettledEvent>` |
| Wyzwalacz | `OrderSettledEvent` (domain event z `Order.Settle`, publikowany po commicie) |
| Wysyła | `RequestPayoutCommand` (`ISender.Send`) |
| Zależności | `ISender`, `ILogger<OrderSettledEventHandler>` |

**Reguły w kodzie**

| # | Warunek | Wyjście | Miejsce |
|---|---|---|---|
| 1 | `notification.PayoutMethod != PayoutMethod.BankTransfer` | `return` | [OrderSettledEventHandler.cs:13](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Application/OrderSettledEventHandler.cs#L13) |

**Obsługa wyniku wywołania**

| Wywołanie | Gdy `IsFailed` | Miejsce |
|---|---|---|
| `sender.Send(RequestPayoutCommand)` | `logger.LogWarning`, brak ponowienia, brak propagacji błędu | [OrderSettledEventHandler.cs:19](https://git.example/skup/-/blob/a1b2c3d/src/Payments.Application/OrderSettledEventHandler.cs#L19) |

**Dane:** brak bezpośrednich odczytów i zapisów. Dane pochodzą z eventu.

## Uwagi zespołu

(treść dołączana z `StartPayoutAfterSettlement.notes.md`, jeśli istnieje)
