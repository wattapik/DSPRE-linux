const vscode = require('vscode');
const { LanguageClient } = require('vscode-languageclient/node');

let client;

function startClient() {
  const config = vscode.workspace.getConfiguration('dspreLua');
  const command = config.get('server.command') || ['dspre-lua-ls'];
  const args = [...command.slice(1), '--stdio'];
  const databases = config.get('server.databases');
  if (databases) args.push('--databases', databases);

  client = new LanguageClient(
    'dspreLua',
    'DSPRE Lua',
    { command: command[0], args },
    {
      // Only the Lua files inside a project's expanded/scripts folder; any other Lua stays with whatever else handles it.
      documentSelector: [{ scheme: 'file', language: 'lua', pattern: '**/expanded/scripts/*.lua' }],
      synchronize: { fileEvents: vscode.workspace.createFileSystemWatcher('**/expanded/scripts/global.lua') },
    },
  );
  return client.start();
}

async function activate(context) {
  context.subscriptions.push(
    vscode.commands.registerCommand('dspreLua.restartServer', async () => {
      if (client) await client.stop();
      await startClient();
    }),
  );
  try {
    await startClient();
  } catch (error) {
    vscode.window.showErrorMessage(`DSPRE Lua: could not start the language server (${error.message}). Set dspreLua.server.command.`);
  }
}

function deactivate() {
  return client ? client.stop() : undefined;
}

module.exports = { activate, deactivate };
