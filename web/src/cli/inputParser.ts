import { findSlash, LOCAL_COMMANDS, PASSTHROUGH_COMMANDS, SlashCommand } from './slashCommands';
import { extractMentions } from './mentions';

export type ParsedInput = {
  raw: string;
  cleanText: string;
  slash: SlashCommand | null;
  slashArgs: string;
  mentions: string[];
  effectiveMode: 'ask' | 'plan' | 'build' | null;
};

export function parseInput(raw: string): ParsedInput {
  const { cmd, args } = findSlash(raw);
  const mentions = extractMentions(raw);
  let cleanText = raw;
  let effectiveMode: ParsedInput['effectiveMode'] = null;
  if (cmd) {
    effectiveMode = (cmd.mode as any) ?? null;
    // For slash commands, cleanText is args if command is mode-forcing; otherwise keep raw without slash prefix but keep args
    if (cmd.mode) {
      cleanText = args;
    } else if (PASSTHROUGH_COMMANDS.includes(cmd.name)) {
      // keep as natural language for LLM, but also expose slash for HostPage shortcut
      cleanText = raw.replace(/^\//, '');
    } else if (LOCAL_COMMANDS.includes(cmd.name)) {
      cleanText = '';
    }
  }
  // Remove @mentions tokens from cleanText? Keep them but they'll be expanded server-side via context injection
  return { raw, cleanText: cleanText.trim(), slash: cmd, slashArgs: args, mentions, effectiveMode };
}
