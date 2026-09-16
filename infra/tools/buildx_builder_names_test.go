package store

import (
    "io"
    "testing"

    "github.com/docker/buildx/util/confutil"
    "github.com/google/uuid"
    "github.com/stretchr/testify/require"
)

func TestGeneratedBuilderNamesRoundTripWithoutCollisions(t *testing.T) {
    s, err := New(confutil.NewConfig(nil, confutil.WithDir(t.TempDir())))
    require.NoError(t, err)
    txn, release, err := s.Txn()
    require.NoError(t, err)
    defer release()
    seen := map[string]bool{}
    for range 100 {
        name, err := GenerateName(txn)
        require.NoError(t, err)
        require.False(t, seen[name])
        seen[name] = true
        _, err = ValidateName(name)
        require.NoError(t, err)
        require.NoError(t, txn.Save(&NodeGroup{Name: name, Driver: "docker-container"}))
        stored, err := txn.NodeGroupByName(name)
        require.NoError(t, err)
        require.Equal(t, name, stored.Name)
    }
}

type repeatedBuilderEntropy struct{}
func (repeatedBuilderEntropy) Read(p []byte) (int, error) {
    clear(p)
    return len(p), nil
}
type failedBuilderEntropy struct{}
func (failedBuilderEntropy) Read([]byte) (int, error) { return 0, io.ErrUnexpectedEOF }

func TestBuilderNameCollisionBudgetAndEntropyFailure(t *testing.T) {
    // This test is deliberately nonparallel because uuid's entropy source is global.
    uuid.SetRand(repeatedBuilderEntropy{})
    defer uuid.SetRand(nil)
    s, err := New(confutil.NewConfig(nil, confutil.WithDir(t.TempDir())))
    require.NoError(t, err)
    txn, release, err := s.Txn()
    require.NoError(t, err)
    defer release()
    name, err := GenerateName(txn)
    require.NoError(t, err)
    require.NoError(t, txn.Save(&NodeGroup{Name: name, Driver: "docker-container"}))
    _, err = GenerateName(txn)
    require.ErrorContains(t, err, "failed to generate random name")
    stored, err := txn.NodeGroupByName(name)
    require.NoError(t, err)
    require.Equal(t, "docker-container", stored.Driver)
    uuid.SetRand(failedBuilderEntropy{})
    _, err = GenerateName(txn)
    require.ErrorContains(t, err, "generate builder name")
}
