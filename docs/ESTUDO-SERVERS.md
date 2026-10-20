# Estudo — SA-MP 0.3.7-R3 e open.mp v1.6.7 (Linux)

Fontes dos pacotes:
- **open.mp v1.6.7.3218** — release oficial GitHub (`open.mp-linux-x86.tar.gz`, 31MB)
- **SA-MP 0.3.7 R3** — último pacote Linux do SA-MP (`samp037svr_R3.tar.gz`, 1.5MB)

Ambos extraídos em `/tmp/opencode/study/{samp,omp}` para referência.

---

## 1. Arquitetura dos pacotes

### SA-MP (`samp03/`)
```
samp03svr          → binário principal (ELF 32-bit i386)
samp-npc           → processo de NPC/bot separado
announce           → utilitário de broadcast LAN
server.cfg         → ÚNICA config (formato chave valor, texto puro)
gamemodes/         → .amx (compilado) + .pwn (fonte)
filterscripts/     → idem
npcmodes/          → scripts de NPC (recordings/)
scriptfiles/       → dados de runtime do gamemode
include/           → includes Pawn
```

### open.mp (`Server/`)
```
omp-server         → binário principal (ELF 32-bit i386, ~29MB)
config.json        → config principal (JSON)
components/        → 22 componentes .so (arquitetura modular!)
  ├── LegacyConfig.so  → lê server.cfg LEGADO do SA-MP (retrocompat!)
  ├── LegacyNetwork.so → aceita clientes 0.3.7
  └── Pawn.so, Vehicles.so, TextDraws.so, ...
gamemodes/         → .amx + .pwn (derby, gungame, simpletdm)
filterscripts/     → vazio por padrão
npcmodes/recordings/
models/            → skins custom (artwork)
scriptfiles/       → dados de runtime
qawno/             → editor + includes Pawn
bans.json          → lista de bans
```

## 2. Config — onde ficam as "slots" (o que o motor deve gerenciar)

### SA-MP — `server.cfg` (texto, `chave valor`)
| Chave | O que é | Default |
|---|---|---|
| `maxplayers` | **SLOTS** (limite de jogadores) | 50 |
| `port` | porta UDP do jogo | 7777 |
| `hostname` | nome do servidor | SA-MP 0.3 Server |
| `gamemode0` | gamemode principal (com delay: `nome 1`) | grandlarc 1 |
| `filterscripts` | filterscripts ativos | base gl_actions ... |
| `rcon_password` | senha RCON | changeme |
| `maxnpc` | slots de NPC/bot | 0 |
| `announce` | listar no masterlist | 0 |
| `query` | responder query (o motor usa isso p/ status!) | 1 |
| `lanmode`, `onfoot_rate`, `incar_rate`, `weapon_rate`, `stream_distance`, `stream_rate` | tuning de rede | — |

**O motor gerencia**: `maxplayers` (slots), `port`, `hostname`, `rcon_password`, `gamemode0`, `filterscripts`, `announce`, `query`.

### open.mp — `config.json`
| Caminho | O que é | Default |
|---|---|---|
| `max_players` | **SLOTS** | 50 |
| `network.port` | porta UDP | 7777 |
| `name` | hostname | open.mp server |
| `pawn.main_scripts` | gamemode(s) | ["gungame 1"] |
| `pawn.side_scripts` | filterscripts | [] |
| `pawn.legacy_plugins` | plugins .so legados | [] |
| `rcon.password` / `rcon.enable` | RCON | false/changeme1 |
| `password` | senha de entrada | "" |
| `network.allow_037_clients` | aceitar clientes SA-MP | true |
| `network.use_omp_encryption` | cripto omp | false |
| `logging.file` | log | log.txt |

**open.mp também aceita `server.cfg` legado** (via `LegacyConfig.so`) → o motor pode usar UM formato unificado de template e gerar a config certa por tipo.

## 3. Restrição crítica: binários 32-bit (i386)

Ambos `samp03svr` e `omp-server` são **ELF 32-bit**:
- Host precisa de libs i386 (`libc6:i386`, `libstdc++6:i386`) ou container/debootstrap 32-bit.
- O motor (C#, .NET 8, x64) não é afetado — mas ao **spawnar o processo** tem que garantir que o ambiente do host suporta i386 (checar no healthcheck).
- Porta default 7777/UDP (jogo+query). A porta SFTP do motor (8821/TCP) não conflita.

## 4. Console do processo
- SA-MP: lê stdin (rcon remoto via `rcon` console) e escreve log no stdout + `server_log.txt`.
- open.mp: idem, stdout + `log.txt` (configurável), console interativo.
- → O motor usa `Process` com stdout/stderr redirecionados (ring buffer) e stdin pra comandos de console.

## 5. Decisões de layout (propostas)
```
/home/nsprovider/servers/{userId}/{serverId}/   → cada servidor (serverId = UUID)
/home/nsprovider/data/motor.db                  → estado (SQLite)
/home/nsprovider/data/templates/samp/           → pacote SA-MP extraído
/home/nsprovider/data/templates/openmp/         → pacote open.mp extraído
```
`serverId` = UUID v4; `userId` = UUID v4. Na criação: copia template → gera config
com `maxplayers` (slots), porta única (alocada do pool 7777+), `rcon_password` aleatório.

## 6. Portas usadas pelo motor
| Porta | Uso |
|---|---|
| 7777+ UDP | jogo (uma por servidor, alocada do pool) |
| 8821 TCP | SFTP do motor (arquivos dos servidores) |
| API interna | REST local p/ integração futura |
