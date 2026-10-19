import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { EventEmitter } from 'node:events';
import path from 'node:path';
import readline from 'node:readline';
import { app } from 'electron';

interface ResponseMessage { kind: 'response'; id: number; result?: unknown; error?: { code: string; message: string }; }
interface EventMessage { kind: 'event'; event: string; data: unknown; }
type BackendMessage = ResponseMessage | EventMessage;

export class BackendClient extends EventEmitter {
  private child?: ChildProcessWithoutNullStreams;
  private nextId = 1;
  private failureSummary?: string;
  private readonly pending = new Map<number, { resolve(value: unknown): void; reject(error: Error): void }>();

  start(): void {
    if (this.child) return;
    this.failureSummary = undefined;
    const command = app.isPackaged ? path.join(process.resourcesPath, 'backend', 'CatAssistant.Backend.exe') : 'dotnet';
    const args = app.isPackaged ? [] : ['run', '--project', path.resolve(__dirname, '..', '..', '..', 'CatAssistant.Backend', 'CatAssistant.Backend.csproj'), '--no-launch-profile'];
    this.child = spawn(command, args, { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
    readline.createInterface({ input: this.child.stdout }).on('line', line => this.acceptLine(line));
    readline.createInterface({ input: this.child.stderr }).on('line', line => {
      console.error(`[backend] ${line}`);
      if (line.startsWith('Backend fatal error:') || (!this.failureSummary && line.startsWith('Unhandled exception.')))
        this.failureSummary = line.replace(/^Backend fatal error:\s*/, '').trim();
    });
    this.child.on('exit', code => {
      this.child = undefined;
      const message = this.failureSummary ? `后台服务已退出：${this.failureSummary}` : '后台服务已退出。';
      for (const pending of this.pending.values()) pending.reject(new Error(message));
      this.pending.clear();
      this.emit('event', { event: 'backend.exited', data: { code, message } });
    });
    this.child.on('error', error => this.emit('error', error));
  }

  invoke<T = unknown>(method: string, params?: Record<string, unknown>): Promise<T> {
    if (!this.child) return Promise.reject(new Error('后台服务尚未启动。'));
    const id = this.nextId++;
    const request = JSON.stringify({ version: 1, id, method, params });
    return new Promise<T>((resolve, reject) => {
      this.pending.set(id, { resolve: value => resolve(value as T), reject });
      this.child!.stdin.write(`${request}\n`, error => {
        if (!error) return;
        this.pending.delete(id); reject(error);
      });
    });
  }

  async stop(): Promise<void> {
    const child = this.child;
    if (!child) return;
    try { await Promise.race([this.invoke('app.shutdown'), new Promise((_, reject) => setTimeout(() => reject(new Error('shutdown timeout')), 2500))]); }
    catch { child.kill(); }
    child.stdin.end();
    if (child.exitCode === null) await Promise.race([new Promise<void>(resolve => child.once('exit', () => resolve())), new Promise<void>(resolve => setTimeout(() => { child.kill(); resolve(); }, 1500))]);
  }

  private acceptLine(line: string): void {
    let message: BackendMessage;
    try { message = JSON.parse(line) as BackendMessage; }
    catch { console.error('[backend] invalid protocol line'); return; }
    if (message.kind === 'event') { this.emit('event', message); return; }
    const pending = this.pending.get(message.id);
    if (!pending) return;
    this.pending.delete(message.id);
    if (message.error) pending.reject(Object.assign(new Error(message.error.message), { code: message.error.code }));
    else pending.resolve(message.result);
  }
}
