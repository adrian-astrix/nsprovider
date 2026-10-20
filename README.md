<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/banner.svg">
  <img src="assets/banner.svg" alt="NsProvider — motor de hosting SA-MP / open.mp" width="860">
</picture>

### Motor de hosting de servidores SA-MP / open.mp — escrito em C# / .NET 8

Daemon self-contained estilo "wings": cria, deleta, atualiza slots, console, SFTP,
arquivos e limites de recursos — **sem Docker, sem painel web, sem dependências externas**.
Pronto pra integração web no futuro via REST.

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
 REST ──▶  ASP.NET Core (8383)         SFTP embutido (8821)  │
 painel │  ServerManager · ProcessManager   FxSsh vendorado   │
 futuro │  PortAllocator · TemplateManager  chroot por user   │
        │  ConfigGenerator · RconClient     auth no SQLite    │
        │                                                     │
        │  cgroups v2 ── memória · cpu · pids  (sem Docker!)  │
        │  watchdog   ── quota de disco por servidor           │
        │  reaper     ── mata órfãos no boot                   │
        └──────┬──────────────────┬──────────────────┬────────┘
               ▼                  ▼                  ▼
        ┌─────────────┐   ┌─────────────┐   ┌─────────────┐
        │  samp03svr  │   │ omp-server  │   │ omp-server  │   ...
        │  uid 10000  │   │ uid 10001   │   │ uid 10002   │
        │  porta 7777 │   │ porta 7778  │   │ porta 7779  │
        └─────────────┘   └─────────────┘   └─────────────┘
   /home/nsprovider/servers/{userId}/{serverId}   (UUIDs)
```

## Destaques

| | |
|---|---|
| 🧩 **Two engines, um motor** | SA-MP 0.3.7-R3 e open.mp 1.6.7 com templates instaláveis via API (`zip`/`tar.gz`) |
| 🎛️ **Slots na mão** | `maxPlayers` de 1 a 1000, porta UDP alocada do pool 7777+, config reescrita em tempo real |
| 🖥️ **Console real** | leitura do log do jogo + eventos do motor + comandos via **RCON UDP** (protocolo legacy de 11 bytes) |
| 📁 **Arquivos** | listar, criar pasta, deletar, upload, download — com path traversal bloqueado |
| 🔐 **SFTP embutido** | porta 8821, chroot por usuário, autenticação no banco (PBKDF2 100k), FxSsh vendorado (MIT) |
| ⚙️ **Limites sem Docker** | cgroups v2: memória, CPU e nº de processos por servidor |
| 💾 **Quota de disco** | watchdog suspende o servidor que estourar a quota |
| 🧹 **Higiene** | reaper de órfãos no boot, chown automático, IDs nunca root |
| 🔑 **API protegível** | `X-Api-Key` opcional, pronto pra painel web futuro |

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
# usuário (também é o login SFTP)
curl -X POST -H "Content-Type: application/json" \
  -d '{"name":"adrian","password":"senha123"}' http://127.0.0.1:8383/api/users

# instalar template (SA-MP ou open.mp)
curl -X POST --data-binary @omp-server.tar.gz http://127.0.0.1:8383/api/templates/openmp

# criar servidor (slots = maxPlayers)
curl -X POST -H "Content-Type: application/json" \
  -d '{"type":"openmp","maxPlayers":100,"hostname":"Meu servidor"}' \
  http://127.0.0.1:8383/api/users/{userId}/servers

# ligar, mandar comando, atualizar slots
curl -X POST http://127.0.0.1:8383/api/servers/{serverId}/start
curl -X POST -H "Content-Type: application/json" \
  -d '{"command":"echo oi"}' http://127.0.0.1:8383/api/servers/{serverId}/console
curl -X PATCH -H "Content-Type: application/json" \
  -d '{"maxPlayers":200}' http://127.0.0.1:8383/api/servers/{serverId}
```

<details>
<summary><b>📖 Referência completa da API REST</b></summary>

| Método | Rota | Descrição |
|---|---|---|
| GET | `/health` | status do motor |
| POST | `/api/users` | criar usuário `{name, password}` |
| GET | `/api/users` | listar usuários |
| PATCH | `/api/users/{id}/password` | trocar senha |
| DELETE | `/api/users/{id}` | deletar usuário |
| POST | `/api/users/{userId}/servers` | criar servidor |
| GET | `/api/users/{userId}/servers` | servidores do usuário |
| GET | `/api/servers` | todos os servidores |
| GET | `/api/servers/{id}` | detalhe + status + recursos |
| PATCH | `/api/servers/{id}` | atualizar slots/hostname/rcon/gamemode |
| DELETE | `/api/servers/{id}` | deletar (para processo, apaga pasta) |
| POST | `/api/servers/{id}/start` · `/stop` · `/restart` | ciclo de vida |
| GET | `/api/servers/{id}/console?lines=N` | console |
| POST | `/api/servers/{id}/console` | comando RCON `{command}` |
| GET | `/api/servers/{id}/files?path=` | listar arquivos |
| POST | `/api/servers/{id}/files/mkdir` | criar pasta |
| DELETE | `/api/servers/{id}/files?path=` | deletar |
| GET | `/api/servers/{id}/files/download?path=` | baixar |
| POST | `/api/servers/{id}/files/upload?path=` | upload |
| GET | `/api/templates` | templates instalados |
| POST | `/api/templates/{samp\|openmp}` | instalar template (body binário) |

Variáveis de ambiente: `NSP_BASE` (raiz) · `NSP_API_URL` (8383) · `NSP_SFTP_PORT` (8821) ·
`NSP_API_KEY` · `NSP_MEM_MB` · `NSP_CPU_CORES` · `NSP_PIDS_MAX` · `NSP_DISK_MB` · `NSP_DISK_CHECK_SECS`

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
├── NsProvider.Core    # ServerManager · ProcessManager · CgroupManager · RconClient · SQLite
├── NsProvider.Sftp    # SftpHost (FxSsh vendorado, chroot por usuário)
└── NsProvider.Api     # host ASP.NET Core + endpoints REST
third_party/FxSsh/     # biblioteca SSH/SFTP vendorada (MIT)
docs/                  # estudo dos pacotes SA-MP / open.mp
```

## Roadmap

- [ ] WebSocket pro console em tempo real
- [ ] JWT na API
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
