# Instrukcje projektu FluentMediaGallery

Pracujemy nad publiczną, natywną galerią multimediów Windows 11: https://github.com/EudoksjuszSzalony/FluentMediaGallery.

Na początku każdej rozmowy odczytaj aktualną gałąź develop, AGENTS.md i HANDOFF.md; HANDOFF.md aktualizuj po każdej istotnej zmianie, zapisując aktualny cel, wykonane wdrożenia, wyniki testów, błędy, decyzje i następny krok. Nie każ powtarzać danych już tam zapisanych.

Wprowadzaj wszystkie zmiany na jednej gałęzi develop, bez mnożenia feature branches. Main wyłącznie dla zweryfikowanych wersji. GitHub Actions nie uruchamiaj przy pushach/PR; oszczędzamy limit na releases i assets. Preferuj bezpośrednie commity w repo i konkretne postępy, nie same plany.

Główny komputer użytkownika to Dominator z Windowsem, repo lokalnie w %USERPROFILE%\Documents\FluentMediaGallery. Remote Desktop Commander obsługuje pomocniczą VM: używaj tylko w razie potrzeby, nie traktuj jej jako weryfikacji na Dominatorze. Unikaj długich operacji zdalnych.

Dla każdego skryptu PowerShell napisz, czy uruchamiać jako USER, czy ADMINISTRATOR. Podawaj jeden kompletny blok "& { ... }", z $ErrorActionPreference='Stop', kontrolą $LASTEXITCODE po poleceniach git/dotnet/CLI i natychmiastowym przerwaniem przy błędzie. Nie stosuj force resetów, bez pytania nie nadpisuj lokalnych zmian. Testuj samodzielnie, gdy możesz; resztę testów wykonuję ja na Dominatorze i podaję wynik.

Pamiętaj o porządku na dysku: po etapie identyfikuj niepotrzebne buildy, katalogi tymczasowe i stare podglądy, najpierw pokaż listę do usunięcia. Czyść tylko bezpiecznie odtwarzalne artefakty. Nigdy nie kasuj oryginalnych zdjęć/wideo, bazy SQLite, kopii zapasowych, źródeł, .git ani istotnych danych użytkownika.

Cel aplikacji: styl Galerii Windows 11/WinUI 3, niezawodne ulubione zdjęć i filmów z widoczną ikonką, odtwarzanie filmów, indeks SQLite bez skanowania wszystkiego na starcie, cache miniaturek, wirtualizacja, monitorowanie zmian także podczas wyłączonej aplikacji, obsługa jawnie wskazanych katalogów lokalnych i UNC/SMB. Nie dodawaj automatycznie udziałów sieciowych. Biblioteka ma ponad 201 tys. plików.

Ulubione według System.Rating: 4 lub 5 gwiazdek to ulubione, 1 gwiazdka to usunięte z ulubionych. Po dodaniu zapisuj 4 gwiazdki (zachowuj istniejące 5), po usunięciu 1. Zapis do pliku weryfikuj odczytem, a przy braku wsparcia formatu zachowuj zmianę i błąd w SQLite. Testuj modyfikowanie metadanych na KOPIACH, nigdy masowo na bibliotece bez sprawdzenia. Niedostępny NAS lub niepełny skan nie oznaczają usunięcia plików.

Pisz po polsku, konkretnie. Wyraźnie odróżniaj kod zatwierdzony w repo od kodu skompilowanego i rzeczywiście przetestowanego. Nie odpalaj GitHub Actions bez prośby.