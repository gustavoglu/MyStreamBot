# MyStreamBot

Bot para lives integrado ao AxelChat via WebSocket.

## Pontos por mensagem

A regra atual de recompensa é:

- Mensagem válida diferente da mensagem anterior do mesmo usuário: **+10 pontos e +10 XP**.
- Mensagem igual à mensagem anterior consecutiva: **0 pontos**.
- A comparação ignora maiúsculas/minúsculas e espaços extras.
- A comparação **não remove acentos**.
- A comparação é feita por usuário + plataforma.
- Se o usuário enviar A, B, A, as três mensagens podem receber pontos.
- Mensagens vazias ou marcadas como deletadas não recebem pontos.

Cada recompensa gera uma `PointTransaction` do tipo `MessageReward`.

## Histórico local de testes

O Worker grava um histórico em JSON Lines (`.jsonl`) no diretório:

```text
Logs/
```

O arquivo é criado por dia:

```text
Logs/axelchat-YYYY-MM-DD.jsonl
```

O histórico inclui:

- mensagens recebidas;
- usuário, plataforma e UserId;
- avatar;
- texto da mensagem;
- MessageId;
- resultado da recompensa (`REWARDED` ou `IGNORED`);
- pontos concedidos;
- saldo após a recompensa;
- motivo da decisão;
- JSON original recebido do AxelChat;
- eventos brutos do AxelChat;
- início, encerramento e erros do Worker.

Esses arquivos podem ser enviados posteriormente para análise dos testes em live.

## Deduplicação persistente de mensagens

O Worker não depende mais apenas de memória para evitar recompensas duplicadas.

- `MessageId` do AxelChat é usado como identidade principal do evento.
- Quando não existe `MessageId`, é usado `Platform + UserId + PublishedAt + mensagem normalizada`.
- Se também não houver `PublishedAt`, é usado um hash do payload.
- A chave fica em `PointTransactions.SourceMessageKey` com índice único no SQLite.
- `StreamerUser.LastChatMessageNormalized` é persistido para manter a regra de mensagens consecutivas após reiniciar o Worker.
- O `Program.cs` atualiza automaticamente bancos existentes adicionando as novas colunas e o índice.

Portanto, reiniciar o Worker ou reconectar ao AxelChat não deve conceder novamente pontos por um evento já processado.
