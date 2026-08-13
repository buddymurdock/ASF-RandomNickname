# ASF-RandomNickname

Плагин для **[ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm)**, который для каждого залогиненного бота через случайные, довольно длинные интервалы (недели-месяцы) меняет отображаемое имя (persona name) на случайный ник из пула — имитация того, что живой человек изредка меняет никнейм, а не держит один и тот же годами.

У каждого бота свой независимый цикл: раз в случайное число дней в диапазоне `[MinDelayDays; MaxDelayDays]` бот выбирает случайный ник из пула кандидатов и выставляет его тем же публичным механизмом (`SteamFriends.SetPersonaName`), которым Steam-клиент сам меняет ник. Один и тот же ник подряд два раза не выбирается (если в пуле есть из чего выбрать).

Смена ника не трогает онлайн-статус/игру бота — `SetPersonaName` переотправляет уже закэшированное текущее состояние вместе с новым именем, поэтому плагин совместим с [RandomOnlineStatus](https://github.com/buddymurdock/ASF-RandomOnlineStatus) и другими плагинами, управляющими статусом независимо.

## Источник никнеймов

По умолчанию используется бандлированный список ~80 нейтральных никнеймов (без NSFW/политики/реальных имён), упакованный в `nicknames.json` рядом с плагином.

## Установка

1. Скачайте архив плагина из [Releases](../../releases) и распакуйте в папку `plugins` рядом с ASF (создайте подпапку с именем плагина).
2. Перезапустите ASF.

## Конфигурация

Настройки задаются **глобально**, в `ASF.json`, как дополнительные (нераспознанные ASF) свойства верхнего уровня:

```json
{
	"RandomNicknameEnabled": true,
	"RandomNicknameMinDelayDays": 14,
	"RandomNicknameMaxDelayDays": 60,
	"RandomNicknameUseBundledNicknames": true,
	"RandomNicknameNicknames": []
}
```

| Свойство | Тип | По умолчанию | Описание |
| --- | --- | --- | --- |
| `RandomNicknameEnabled` | `bool` | `false` | Включает/выключает плагин. |
| `RandomNicknameMinDelayDays` | `ushort`, дни | `14` | Нижняя граница случайной паузы между сменами ника. |
| `RandomNicknameMaxDelayDays` | `ushort`, дни | `60` | Верхняя граница случайной паузы между сменами ника. |
| `RandomNicknameUseBundledNicknames` | `bool` | `false` | Добавлять ли в пул кандидатов ники из бандла `nicknames.json` (см. выше). |
| `RandomNicknameNicknames` | `string[]` | `[]` | Свой список ников (до 32 символов, лимит Steam), добавляется к бандлу (если он включён) или используется как единственный источник (если бандл выключен). |

Если `MinDelayDays` больше `MaxDelayDays`, значения меняются местами автоматически. Если итоговый пул пуст (бандл выключен и свой список не задан), плагин один раз пишет предупреждение и ничего не делает.

## Сборка

Проект использует **[ASF-PluginTemplate](https://github.com/JustArchiNET/ASF-PluginTemplate)** и собирается вместе с исходниками ASF, подключёнными как git submodule:

```sh
git clone --recurse-submodules https://github.com/buddymurdock/ASF-RandomNickname.git
cd ASF-RandomNickname
dotnet build -c Release
```

Если репозиторий уже склонирован без `--recurse-submodules`, подтяните submodule отдельно:

```sh
git submodule update --init --recursive
```

## Лицензия

Apache-2.0, см. [LICENSE.txt](LICENSE.txt).
