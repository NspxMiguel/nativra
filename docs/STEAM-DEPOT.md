# Baixar um jogo da Steam — o protocolo, com os números conferidos

Tudo abaixo foi lido dos `.proto` oficiais em
`github.com/SteamDatabase/Protobufs/tree/master/steam` em 19/09/2026, não de
memória. Os números de campo são o que o codec escrito à mão precisa.

## Por que não dá para ficar só na Web API

`GetOwnedGames` (biblioteca) é Web API pura e já funciona com o `access_token`
do login por QR. **Baixar não é**: a chave que descriptografa o depot só sai
pela conexão de cliente (CM), e a lista de depots/manifestos de um app vem do
PICS, que também é CM. Então o app precisa falar o protocolo de cliente.

O alívio: o CM moderno aceita **websocket sobre TLS**, e aí não existe o
handshake de criptografia antigo — o TLS já cobre. Sobra o enquadramento de
mensagem, que é simples.

## Conexão

1. `GET https://api.steampowered.com/ISteamDirectory/GetCMListForConnect/v1/`
   `?cellid=0&cmtype=websockets` → lista de `endpoint` (`host:porta`).
2. Conectar em `wss://<endpoint>/cmsocket/`.
3. Cada mensagem binária do websocket é **um** pacote Steam:

```
uint32  emsg | 0x80000000      (bit alto = corpo em protobuf)
uint32  tamanho do cabeçalho
bytes   CMsgProtoBufHeader
bytes   corpo (protobuf da mensagem)
```

`CMsgProtoBufHeader`: `steamid` 1 (fixed64), `client_sessionid` 2 (int32),
`routing_appid` 3, `jobid_source` 10 (fixed64), `jobid_target` 11 (fixed64),
`target_job_name` 12 (string), `eresult` 13 (int32), `error_message` 14,
`realm` 32.

Chamada de serviço unificado: `target_job_name` =
`"ContentServerDirectory.GetManifestRequestCode#1"`, `jobid_source` = um número
nosso, e a resposta volta com `jobid_target` igual a ele.

## Logon

`CMsgClientLogon` (EMsg 5514, a confirmar contra `EMsg.cs`):

| campo | nº | o que pôr |
| --- | --- | --- |
| `protocol_version` | 1 | 65580 |
| `cell_id` | 3 | 0 |
| `client_package_version` | 5 | 1771 |
| `client_language` | 6 | `"english"` |
| `client_os_type` | 7 | 20 (Windows 10) |
| `should_remember_password` | 8 | false |
| `machine_id` | 30 | bytes; pode ir vazio |
| `machine_name` | 96 | `"Xbox Series X"` |
| `client_instance_id` | 100 | 0 |
| `supports_rate_limit_response` | 102 | true |
| **`access_token`** | **108** | o `refresh_token` do login por QR |

⚠️ O campo se chama `access_token` mas o que o cliente manda ali é o
**refresh token** — é ele que autentica a sessão de cliente.

`CMsgClientLogonResponse`: `eresult` 1, `heartbeat_seconds` 3, `cell_id` 7,
`client_supplied_steamid` 20 (fixed64). Depois do logon, mandar
`ClientHeartBeat` a cada `heartbeat_seconds`, senão o CM derruba.

## Chave do depot

`CMsgClientGetDepotDecryptionKey` — `depot_id` 1, `app_id` 2.
Resposta: `eresult` 1, `depot_id` 2, `depot_encryption_key` 3 (32 bytes).

## Onde baixar

`ContentServerDirectory.GetServersForSteamPipe#1`
→ pedido: `cell_id` 1, `max_servers` 2 (padrão 20)
→ resposta: `servers` 1, cada um com `type` 1, `cell_id` 3, `load` 4,
   `host` 8, `vhost` 9, `https_support` 12. Interessam os de tipo `SteamCache`
   e `CDN`.

`ContentServerDirectory.GetManifestRequestCode#1`
→ pedido: `app_id` 1, `depot_id` 2, `manifest_id` 3, `app_branch` 4 (`public`)
→ resposta: `manifest_request_code` 1.

URLs no CDN:

```
https://<vhost>/depot/<depotid>/manifest/<manifestid>/5/<request_code>
https://<vhost>/depot/<depotid>/chunk/<sha-do-chunk-em-hex>
```

## Manifesto

O arquivo é um ZIP; dentro, blocos com marcador de 4 bytes:

| marcador | conteúdo |
| --- | --- |
| `0x71F617D0` | `ContentManifestPayload` |
| `0x1F4812BE` | `ContentManifestMetadata` |
| `0x1B81B817` | assinatura |
| `0x32C415AB` | fim |

`ContentManifestPayload.mappings` (campo 1) → `FileMapping`: `filename` 1,
`size` 2, `flags` 3, `sha_filename` 4, `sha_content` 5, `chunks` 6,
`linktarget` 7. Cada `ChunkData`: `sha` 1 (o id do chunk), `crc` 2 (fixed32),
`offset` 3, `cb_original` 4, `cb_compressed` 5.

`ContentManifestMetadata`: `depot_id` 1, `gid_manifest` 2, `creation_time` 3,
`filenames_encrypted` 4, `cb_disk_original` 5, `cb_disk_compressed` 6,
`unique_chunks` 7.

Quando `filenames_encrypted` é verdadeiro, o `filename` vem em base64,
criptografado com a chave do depot (AES-256-CBC, mesmo esquema do chunk).

## Chunk: descriptografar e descomprimir

1. Baixar o chunk (o corpo é opaco).
2. Os **16 primeiros bytes** são o IV criptografado: AES-256-**ECB** com a
   chave do depot devolve o IV em claro.
3. O resto é AES-256-**CBC** com esse IV e a mesma chave.
4. O resultado começa com `VZ` (LZMA da Valve) ou `PK` (zip normal). O `PK`
   é deflate comum; o `VZ` é LZMA com cabeçalho próprio.
5. Conferir o `crc`/`sha` do chunk e escrever em `offset` dentro do arquivo.

## O que ainda falta medir

- Confirmar os valores de EMsg contra `SteamKit/Resources/SteamLanguage/emsg.steamd`
  (ClientLogon, ClientLogOnResponse, ClientGetDepotDecryptionKey e o par
  ServiceMethod/ServiceMethodResponse).
- Descobrir a lista de depots do app: `ClientPICSProductInfoRequest`, que
  também é CM e ainda não foi lido aqui.
- Testar se `GetManifestRequestCode` responde pela Web API
  (`IContentServerDirectoryService`) com o `access_token` — se responder,
  metade disso sai do CM.
