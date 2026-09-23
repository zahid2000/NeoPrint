import { defineConfig } from 'vitest/config';

/**
 * Runner settings for the panel's unit tests.
 *
 * The Angular builder supplies the environment and the compiled sources; this
 * file only tunes the pool. One forked worker, running the spec files in
 * sequence: vitest allows a worker five seconds to start (not configurable),
 * and on Windows starting one per file regularly missed that. Each file sets
 * up its own TestBed anyway, so nothing is lost by running them in order -
 * but they stay isolated, because the interceptor keeps module-level state.
 */
export default defineConfig({
  test: {
    pool: 'forks',
    poolOptions: {
      forks: { singleFork: true },
    },
    fileParallelism: false,
    testTimeout: 15000,
    hookTimeout: 15000,
    teardownTimeout: 15000,
  },
});
