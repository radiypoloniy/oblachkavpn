package main

import (
	"context"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/gofrs/uuid/v5"
	"github.com/sagernet/sing-box/log"
)

func fixtureSecret(t *testing.T) string {
	t.Helper()
	id, err := uuid.NewV4()
	if err != nil {
		t.Fatal(err)
	}
	return id.String()
}

func TestProgramServiceAuthenticationAndMethods(t *testing.T) {
	secret := fixtureSecret(t)
	s := &programService{options: programServiceOptions{Secret: secret}}
	for _, test := range []struct {
		auth, method, path string
		status             int
	}{
		{"", "GET", "/programs", 401}, {"Bearer wrong", "GET", "/programs", 401},
		{"Bearer " + secret, "POST", "/programs", 404}, {"Bearer " + secret, "GET", "/other", 404},
	} {
		r := httptest.NewRequest(test.method, test.path, nil)
		r.Header.Set("Authorization", test.auth)
		w := httptest.NewRecorder()
		s.serve(w, r)
		if w.Code != test.status {
			t.Fatal(w.Code, test)
		}
	}
}

func TestProgramServiceRejectsNonLoopbackConfiguration(t *testing.T) {
	for _, listen := range []string{"0.0.0.0:12345", "192.0.2.1:12345", "127.0.0.1:80", "127.0.0.1:65536"} {
		_, err := newProgramService(context.Background(), log.StdLogger(), "fixture", programServiceOptions{Listen: listen, Secret: strings.Repeat("a", 32)})
		if err == nil {
			t.Fatal("accepted unsafe address", listen)
		}
	}
}
