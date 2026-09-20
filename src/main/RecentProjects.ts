import { readFile } from 'node:fs/promises';
import path from 'node:path';

import { writeJsonSafely } from '../utils/FileSystem.ts';

interface RecentProjectsDocument {
  version: 0;
  projects: string[];
}

export class RecentProjects {
  static readonly maxProjects = 20;
  private readonly file: string;

  constructor(file: string) {
    this.file = file;
  }

  async list(): Promise<string[]> {
    try {
      const value: unknown = JSON.parse(await readFile(this.file, 'utf8'));
      if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('must be an object');
      const document = value as Partial<RecentProjectsDocument>;
      if (document.version !== 0 || !Array.isArray(document.projects)
        || document.projects.some((project) => typeof project !== 'string' || !path.isAbsolute(project))) {
        throw new Error('has an unsupported shape');
      }
      return [...new Set(document.projects.map((project) => path.resolve(project)))].slice(0, RecentProjects.maxProjects);
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'code' in error && error.code === 'ENOENT') return [];
      throw new Error(`Recent projects file ${error instanceof Error ? error.message : 'is invalid'}.`);
    }
  }

  async add(projectPath: string): Promise<void> {
    const resolved = path.resolve(projectPath);
    const projects = (await this.list()).filter((project) => project !== resolved);
    await this.write([resolved, ...projects].slice(0, RecentProjects.maxProjects));
  }

  async remove(projectPath: string): Promise<void> {
    const resolved = path.resolve(projectPath);
    await this.write((await this.list()).filter((project) => project !== resolved));
  }

  private write(projects: string[]): Promise<void> {
    return writeJsonSafely(this.file, { version: 0, projects } satisfies RecentProjectsDocument);
  }
}
