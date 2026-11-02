package main

import (
	"os"
	"runtime/pprof"
	"testing"
	"time"
)

// Opt-in test executable for the loopback resource probe. No profiling endpoint,
// environment switch or test harness is compiled into the production core.
func TestMain(m *testing.M) {
	if file := os.Getenv("OBLACHKA_TEST_CPU_PROFILE"); file != "" && len(os.Args) == 4 && os.Args[1] == "run" {
		go func() {
			// Skip startup and the probe's 90-second idle phase; capture steady load.
			time.Sleep(105 * time.Second)
			output, err := os.Create(file)
			if err != nil {
				return
			}
			defer output.Close()
			if pprof.StartCPUProfile(output) != nil {
				return
			}
			time.Sleep(60 * time.Second)
			pprof.StopCPUProfile()
		}()
		main()
		return
	}
	os.Exit(m.Run())
}
