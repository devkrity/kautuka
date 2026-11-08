#!/usr/bin/env node
import { launchDevelopmentAppliance } from './launch-kautuka-wsl.mjs';

await launchDevelopmentAppliance(process.argv.slice(2));
