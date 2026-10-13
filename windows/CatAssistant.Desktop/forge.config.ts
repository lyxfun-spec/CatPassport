import type { ForgeConfig } from '@electron-forge/shared-types';
import { MakerSquirrel } from '@electron-forge/maker-squirrel';
import { MakerZIP } from '@electron-forge/maker-zip';
import { VitePlugin } from '@electron-forge/plugin-vite';
import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const desktopDirectory = path.dirname(fileURLToPath(import.meta.url));
const backendProject = path.resolve(desktopDirectory, '..', 'CatAssistant.Backend', 'CatAssistant.Backend.csproj');
const backendOutput = path.resolve(desktopDirectory, '.artifacts', 'backend');

const config: ForgeConfig = {
  packagerConfig: {
    asar: true,
    executableName: 'CatAssistant',
    extraResource: [backendOutput]
  },
  rebuildConfig: {},
  hooks: {
    prePackage: async (_config, platform, arch) => {
      if (platform !== 'win32') throw new Error('小猫助手桌面版目前只支持 Windows 打包。');
      const rid = arch === 'arm64' ? 'win-arm64' : 'win-x64';
      execFileSync('dotnet', ['publish', backendProject, '-c', 'Release', '-r', rid, '--self-contained', 'true', '-o', backendOutput], { stdio: 'inherit' });
    }
  },
  makers: [
    new MakerSquirrel({ name: 'CatAssistant', setupExe: 'CatAssistant-Setup.exe' }),
    new MakerZIP({}, ['win32'])
  ],
  plugins: [
    new VitePlugin({
      build: [
        { entry: 'src/main.ts', config: 'vite.main.config.ts', target: 'main' },
        { entry: 'src/preload.ts', config: 'vite.preload.config.ts', target: 'preload' }
      ],
      renderer: [{ name: 'main_window', config: 'vite.renderer.config.ts' }]
    })
  ]
};

export default config;
