<div align="center">

<img src="assets/banner.svg" alt="NsProvider — motor de hosting SA-MP / open.mp" width="860">

### Motor de hosting de servidores SA-MP / open.mp — escrito em C# / .NET 8

Daemon self-contained estilo **wings (Pterodactyl)**: um painel web controla **múltiplos motores**
(múltiplas VPS). O motor cria, deleta, atualiza slots, console, SFTP, arquivos, backups e limites
de recursos — **sem Docker, sem senha de usuário armazenada, sem dependências externas**.

<br>

![.NET](https://img.shields.io/badge/.NET-8.0-0D1B2A?style=for-the-badge&logo=dotnet&logoColor=E0E1DD)
![C#](https://img.shields.io/badge/C%23-12-1B263B?style=for-the-badge&logo=csharp&logoColor=E0E1DD)
![Linux](https://img.shields.io/badge/Linux-x86_32--bits-0D1B2A?style=for-the-badge&logo=linux&logoColor=E0E1DD)
![SA-MP](https://img.shields.io/badge/SA--MP-0.3.7_R3-1B263B?style=for-the-badge&logoColor=E0E1DD)
![open.mp](https://img.shields.io/badge/open.mp-v1.6.7-0D1B2A?style=for-the-badge&logoColor=E0E1DD)
![License](https://img.shields.io/badge/Licença-MIT-1B263B?style=for-the-badge&logoColor=E0E1DD)

![status](https://img.shields.io/badge/status-beta-0D1B2A?style=flat-square&labelColor=1B263B)
![SFTP](https://img.shields.io/badge/SFTP-8821-1B263B?style=flat-square&labelColor=0D1B2A)
![cgroups](https://img.shields.io/badge/isolamento-cgroups_v2-0D1B2A?style=flat-square&labelColor=1B263B)
![RCON](https://img.shields.io/badge/console-RCON_UDP-1B263B?style=flat-square&labelColor=0D1B2A)
![SQLite](https://img.shields.io/badge/estado-SQLite-0D1B2A?style=flat-square&labelColor=1B263B)

**feito por [adriansdk](https://github.com/adrian-astrix)**

</div>

---

## O que é

```
        ┌─────────────────────────────────────────────────────┐
        │                    NsProvider                       │
        │                                                     │
 painel ▶  ASP.NET Core (8383)         SFTP embutido (8821)  │
 (token)│  ServerManager · ProcessManager   FxSsh vendorado   │
        │  PanelClient (auth SFTP)          chroot por user   │
        │  BackupService · SystemStats                        │
        │                                                     │
        │  cgroups v2 ── memória · cpu · pids  (sem Docker!)  │
        │  watchdog   ── quota de disco por servidor           │
        │  backups    ── tar.gz + restore + limites            │
        │  reaper     ── mata órfãos no boot                   │
        └──────┬──────────────────┬──────────────────┬────────┘
               ▼                  ▼                  ▼
        ┌─────────────┐   ┌─────────────┐   ┌─────────────┐
        │  samp03svr  │   │ omp-server  │   │ omp-server  │   ...
        │  uid 10000  │   │ uid 10001   │   │ uid 10002   │
        │  porta 7777 │   │ porta 7778  │   │ porta 7779  │
        └─────────────┘   └─────────────┘   └─────────────┘
   {NSP_BASE}/servers/{userId}/{serverId}   (UUIDs — o painel pode mandar os seus)
```

## Destaques

| | |
|---|---|
| 🧩 **Two engines, um motor** | SA-MP 0.3.7-R3 e open.mp 1.6.7 com templates instaláveis via API (`zip`/`tar.gz`) |
| 🎛️ **Slots na mão** | `maxPlayers` de 1 a 1000, porta UDP alocada do pool 7777+, config reescrita em tempo real |
| 🖥️ **Console real** | leitura do log do jogo + eventos do motor + comandos via **RCON UDP** (protocolo legacy de 11 bytes) |
| 📁 **Arquivos** | listar, criar pasta, renomear, deletar, upload, download — path traversal bloqueado |
| 🔐 **SFTP embutido** | porta 8821, chroot por usuário, senha validada **no painel web** (nada armazenado no motor) |
| 💾 **Backups** | por servidor, `tar.gz`, com restore, download, delete e limites (`NSP_BACKUP_MAX_MB`/`MAX_COUNT`) |
| 📊 **Stats da máquina** | CPU (modelo, cores, threads, uso), memória, disco e servidores (total × rodando) |
| ⚙️ **Limites sem Docker** | cgroups v2: memória, CPU e nº de processos por servidor |
| 💽 **Quota de disco** | watchdog suspende o servidor que estourar a quota |
| 🧹 **Higiene** | reaper de órfãos no boot, chown automático, IDs nunca root |
| 🔑 **Auth de painel** | token obrigatório (`NSP_PANEL_TOKEN`) + CORS travado na origem do painel |

## Início rápido

```bash
# dependências do host (binários dos jogos são 32-bit)
apt-get install -y libc6:i386 libstdc++6:i386 zlib1g:i386 libatomic1:i386

# rodar o motor
dotnet run --project src/NsProvider.Api
# API  → http://127.0.0.1:8383
# SFTP → porta 8821
```

```bash
# usuário — SEM senha: autenticação é coisa do painel (estilo wings/Pterodactyl)
# o painel pode mandar o próprio UUID (mesmo usuário em múltiplos motores)
curl -X POST -H "Content-Type: application/json" -H "X-Api-Key: $TOKEN" \
  -d '{"name":"adrian","id":"11111111-2222-3333-4444-555555555555"}' http://127.0.0.1:8383/api/users

# instalar template (SA-MP ou open.mp)
curl -X POST -H "X-Api-Key: $TOKEN" --data-binary @omp-server.tar.gz http://127.0.0.1:8383/api/templates/openmp

# criar servidor (o painel pode mandar o ID dele também)
curl -X POST -H "Content-Type: application/json" -H "X-Api-Key: $TOKEN" \
  -d '{"type":"openmp","maxPlayers":100,"hostname":"Meu servidor"}' \
  http://127.0.0.1:8383/api/users/{userId}/servers

# ligar, mandar comando, atualizar slots, backup
curl -X POST -H "X-Api-Key: $TOKEN" http://127.0.0.1:8383/api/servers/{serverId}/start
curl -X POST -H "Content-Type: application/json" -H "X-Api-Key: $TOKEN" \
  -d '{"command":"echo oi"}' http://127.0.0.1:8383/api/servers/{serverId}/console
curl -X PATCH -H "Content-Type: application/json" -H "X-Api-Key: $TOKEN" \
  -d '{"maxPlayers":200}' http://127.0.0.1:8383/api/servers/{serverId}
curl -X POST -H "X-Api-Key: $TOKEN" http://127.0.0.1:8383/api/servers/{serverId}/backups
```

## Integração painel web ↔ motor (estilo wings)

Um painel web controla **vários motores** (uma VPS = um motor). Cada motor é configurado
na instalação com **a URL do painel** e **uma key gerada por ele**:

```bash
export NSP_PANEL_URL="https://painel.exemplo.com"   # única origem autorizada (CORS + callbacks)
export NSP_PANEL_TOKEN="key-gerada-pelo-painel"     # key deste motor (obrigatória em toda /api)
```

| Comportamento | Como funciona |
|---|---|
| **Auth da API** | toda roda `/api` exige `X-Api-Key: <token>` ou `Authorization: Bearer <token>` (401 senão); `/health` fica aberto |
| **Origem única** | CORS liberado **só** para a origem de `NSP_PANEL_URL` — browser de outro site é bloqueado |
| **Usuários sem senha** | o motor **não armazena senha nenhuma**; guarda só id, nome e UID do Linux |
| **IDs do painel** | usuário e servidor podem ser criados com UUID escolhido pelo painel (mesmo ID nos vários motores) |
| **Login SFTP** | o motor pergunta ao painel (callback abaixo); resposta negada = desconecta |
| **Limites** | hoje via env (`NSP_BACKUP_MAX_MB` etc.); no futuro o painel define por usuário |

**Contrato do callback de SFTP** (o painel web futuro precisa implementar):

```
POST {NSP_PANEL_URL}/api/remote/sftp/auth
Authorization: Bearer {NSP_PANEL_TOKEN}
{"username": "...", "password": "...", "ip": "..."}

→ 200 {"user_id": "<uuid>", "name": "..."}   autorizado (motor cria o usuário local se não existir)
→ 401/403                                    credenciais inválidas → SFTP recusado
```

Fluxo multi-motor: o painel cadastra o usuário em cada motor (`POST /api/users` com o mesmo UUID),
cria os servidores, e o SFTP do usuário funciona em qualquer motor — a senha mora só no painel.

<details>
<summary><b>📖 Referência completa da API REST</b></summary>

| Método | Rota | Descrição |
|---|---|---|
| GET | `/health` | status do motor (aberto, sem token) |
| GET | `/api/stats` | **estatísticas**: CPU (modelo/cores/threads/uso), memória, disco, servidores total × rodando |
| POST | `/api/users` | criar usuário **sem senha** `{name, id?}` (id = UUID do painel) |
| GET | `/api/users` · `/api/users/{id}` | listar / detalhar |
| DELETE | `/api/users/{id}` | deletar usuário |
| POST | `/api/users/{userId}/servers` | criar servidor (aceita `id` do painel) |
| GET | `/api/users/{userId}/servers` · `/api/servers` | listar |
| GET | `/api/servers/{id}` | detalhe + status + recursos |
| PATCH | `/api/servers/{id}` | atualizar slots/hostname/rcon/gamemode |
| DELETE | `/api/servers/{id}` | deletar (para processo, apaga pasta e backups) |
| POST | `/api/servers/{id}/start` · `/stop` · `/restart` | ciclo de vida |
| GET | `/api/servers/{id}/console?lines=N` | console (log + eventos do motor) |
| POST | `/api/servers/{id}/console` | comando RCON `{command}` |
| GET | `/api/servers/{id}/files?path=` | listar arquivos |
| POST | `/api/servers/{id}/files/mkdir` | criar pasta |
| POST | `/api/servers/{id}/files/rename` | renomear `{from, to}` |
| DELETE | `/api/servers/{id}/files?path=` | deletar |
| GET | `/api/servers/{id}/files/download?path=` | baixar |
| POST | `/api/servers/{id}/files/upload?path=` | upload |
| POST | `/api/servers/{id}/backups` | **criar backup** (assíncrono, status `pending` → `ready`/`failed`) |
| GET | `/api/servers/{id}/backups` · `/{backupId}` | listar / status |
| GET | `/api/servers/{id}/backups/{backupId}/download` | baixar o `.tar.gz` |
| POST | `/api/servers/{id}/backups/{backupId}/restore` | restaurar (para o servidor, limpa e extrai) |
| DELETE | `/api/servers/{id}/backups/{backupId}` | deletar backup |
| GET | `/api/templates` | templates instalados |
| POST | `/api/templates/{samp\|openmp}` | instalar template (body binário) |

Variáveis de ambiente:

| Variável | Default | Descrição |
|---|---|---|
| `NSP_BASE` | `/home/nsprovider` | raiz de tudo |
| `NSP_PANEL_URL` | *(vazio)* | URL do painel web (origem única autorizada + callback SFTP) |
| `NSP_PANEL_TOKEN` | *(vazio)* | key do motor gerada pelo painel (obrigatória em `/api`) |
| `NSP_API_KEY` | *(vazio)* | alternativa local ao token do painel |
| `NSP_API_URL` | `http://127.0.0.1:8383` | porta da API |
| `NSP_SFTP_PORT` | `8821` | porta do SFTP embutido |
| `NSP_BACKUP_MAX_MB` | `2048` | tamanho máximo do conteúdo por backup |
| `NSP_BACKUP_MAX_COUNT` | `5` | backups por servidor |
| `NSP_MEM_MB` / `NSP_CPU_CORES` / `NSP_PIDS_MAX` | `1024` / `1` / `200` | limites cgroup por servidor |
| `NSP_DISK_MB` / `NSP_DISK_CHECK_SECS` | `5120` / `60` | quota de disco + intervalo do watchdog |

</details>

## Segurança — fase 1 (sem Docker)

| Camada | Mecanismo | Protege contra |
|---|---|---|
| Privilégios | jogo roda como UID dedicado (`setpriv`), nunca root | plugin .so malicioso sem dono pra atacar o host |
| Memória/CPU/pids | cgroups v2 nativos por servidor | memory hog, fork bomb, roubo de CPU |
| Disco | watchdog suspende quem estoura a quota | disco enchendo até derrubar a máquina |
| Órfãos | reaper mata processos abandonados no boot | motor morto na força |
| SFTP | chroot por usuário, sem shell/exec | fuga da pasta |

## Estrutura

```
src/
├── NsProvider.Core    # ServerManager · ProcessManager · CgroupManager · BackupService
│                       # PanelClient (auth SFTP) · SystemStats · RconClient · SQLite
├── NsProvider.Sftp    # SftpHost (FxSsh vendorado, chroot por usuário)
└── NsProvider.Api     # host ASP.NET Core + endpoints REST + CORS do painel
third_party/FxSsh/     # biblioteca SSH/SFTP vendorada (MIT)
docs/                  # estudo dos pacotes SA-MP / open.mp
```

## Roadmap

- [ ] WebSocket pro console em tempo real
- [ ] Painel web (o motor já fala o idioma: token, CORS, callback SFTP, IDs externos)
- [ ] Limites de backup/quota definidos pelo painel por usuário
- [ ] Landlock + seccomp (jaula de syscall/filesystem)
- [ ] Backend Docker/Podman opcional (`IServerBackend`) pra hosting público
- [ ] Métricas Prometheus

---

<div align="center">

**feito por [adriansdk](https://github.com/adrian-astrix)** ·
[documentação do estudo](docs/ESTUDO-SERVERS.md) ·
[LICENSE](LICENSE)

<img src="https://img.shields.io/badge/NsProvider-motor%20de%20hosting-0D1B2A?style=flat-square&labelColor=1B263B">

</div>
